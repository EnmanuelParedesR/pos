using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.CashRegister.Contracts;
using Toki.Customers.Contracts;
using Toki.Inventory.Contracts;
using Toki.Sales.Contracts;
using Toki.Settings.Contracts;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.Sync;

/// <summary>Writes a <see cref="NodeHeartbeatV1"/> to the outbox (it goes up with the other events) while the store is connected (TK-190).
/// Only counts and flags, read through the other modules' reports.</summary>
internal static class SendHeartbeat
{
    internal sealed record Command : ICommand<Ok>;

    internal sealed class Handler(SyncDbContext db, InfraDbContext infra, IOutbox outbox, ISalesReports sales, ICashRegisterReports cash,
        ICustomerReports customers, IInventoryReports inventory, ISettingsQueries settings, IFeatureService license, IConfiguration config, TimeProvider clock)
        : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(ct);
            if (connection is null) return Ok.Value;

            var today = BusinessDay.Of(clock.GetUtcNow().UtcDateTime);
            var byDay = await sales.ByDayAsync(today.AddDays(-1), today, ct);
            var days = new List<DailyUsageV1>();
            foreach (var day in new[] { today.AddDays(-1), today })
            {
                var cashiers = await sales.ByCashierAsync(day, day, ct);
                var d = byDay.FirstOrDefault(x => x.Day == day);
                days.Add(new DailyUsageV1(day, d?.SaleCount ?? 0, d?.SalesWithItbis ?? 0, cashiers.Count(x => x.SaleCount > 0), cashiers.Sum(x => x.VoidedCount),
                    d?.Returns ?? 0));
            }
            var shifts = await cash.ShiftsAsync(today.AddDays(-30), today, ct);
            var profile = await settings.GetBusinessProfileAsync(ct);

            outbox.Add(new NodeHeartbeatV1(
                connection.NodeId, AppVersion, RuntimeInformation.OSDescription, UptimeMinutes(),
                shifts.Select(s => s.Register).Distinct().Count(), shifts.Count(s => s.ClosedUtc is null),
                await infra.Outbox.CountAsync(m => m.UploadedUtc == null, ct), CloudUploader.ErrorCode(connection.LastError),
                new NodeFeaturesV1((await customers.ReceivablesAsync(ct)).Any(r => r.Total > 0), null, null, config["Fiscal:Provider"] ?? "Simulated"),
                new NodeSetupV1(!string.IsNullOrWhiteSpace(profile.Name), !string.IsNullOrWhiteSpace(profile.Rnc), (await inventory.ValueAsync(ct)).Count(p => p.IsActive)),
                days, new NodeLicenseV1(license.Status.ToString(), license.EndsUtc)));
            return Ok.Value;
        }

        /// <summary>"1.4.0", without the commit suffix, so versions in the field group together.</summary>
        private static string AppVersion =>
            ((Assembly.GetEntryAssembly() ?? typeof(SendHeartbeat).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
            .Split('+')[0];

        private static int UptimeMinutes()
        {
            using var process = Process.GetCurrentProcess();
            return (int)Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMinutes);
        }
    }
}

/// <summary>Sends a heartbeat on start and every <c>Sync:HeartbeatMinutes</c> (240), unless <c>Sync:Enabled</c> is false (the test host).</summary>
/// <remarks>R8 #21: every upload wakes the cloud's serverless SQL for at least an hour (~0.30 USD); an hourly heartbeat kept it awake all day for
/// every connected store. Every 4 hours it still shows a store as alive well inside the 24-hour "no data" alert.</remarks>
internal sealed class HeartbeatWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<HeartbeatWorker> log) : BackgroundService
{
    public static TimeSpan Interval(IConfiguration config) => TimeSpan.FromMinutes(config.GetValue("Sync:HeartbeatMinutes", 240));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Sync:Enabled", true)) return;
        using var timer = new PeriodicTimer(Interval(config), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<SendHeartbeat.Command, Ok>>().Handle(new SendHeartbeat.Command(), stoppingToken);
                if (result.IsFailure) log.LogWarning("The heartbeat was not saved: {Code}", result.Error!.Code);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "The heartbeat failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
