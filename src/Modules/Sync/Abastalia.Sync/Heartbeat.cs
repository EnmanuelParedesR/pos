using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.CashRegister.Contracts;
using Abastalia.Customers.Contracts;
using Abastalia.Fiscal.Contracts;
using Abastalia.Identity.Contracts;
using Abastalia.Inventory.Contracts;
using Abastalia.Sales.Contracts;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Sync;

/// <summary>Writes a <see cref="NodeHeartbeatV1"/> to the outbox (it goes up with the other events) while the store is connected (TK-190).
/// Only counts and flags, read through the other modules' reports.</summary>
internal static class SendHeartbeat
{
    internal sealed record Command : ICommand<Ok>;

    internal sealed class Handler(SyncDbContext db, InfraDbContext infra, IOutbox outbox, ISalesReports sales, ICashRegisterReports cash,
        ICustomerReports customers, IInventoryReports inventory, ISettingsQueries settings, IFeatureService license, IFiscalReports fiscal, IDevicePeripherals peripherals, IConfiguration config, TimeProvider clock)
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
            var fiscalHealth = await fiscal.HealthAsync(ct);
            // Null until some register reports its peripherals (they live on the PC, not here).
            var devices = await peripherals.SummaryAsync(ct);
            bool? Any(int count) => devices.Reported == 0 ? null : count > 0;

            outbox.Add(new NodeHeartbeatV1(
                connection.NodeId, AppVersion, RuntimeInformation.OSDescription, UptimeMinutes(),
                shifts.Select(s => s.Register).Distinct().Count(), shifts.Count(s => s.ClosedUtc is null),
                await infra.Outbox.CountAsync(m => m.UploadedUtc == null, ct), CloudUploader.ErrorCode(connection.LastError),
                new NodeFeaturesV1((await customers.ReceivablesAsync(ct)).Any(r => r.Total > 0), Any(devices.WithScale), Any(devices.WithPrinter), config["Fiscal:Provider"] ?? "Simulated"),
                new NodeSetupV1(!string.IsNullOrWhiteSpace(profile.Name), !string.IsNullOrWhiteSpace(profile.Rnc), (await inventory.ValueAsync(ct)).Count(p => p.IsActive)),
                days, new NodeLicenseV1(license.Status.ToString(), license.EndsUtc, license.LicenseJws),
                new NodeFiscalV1(fiscalHealth.RejectedWithoutReplacement, fiscalHealth.OldestPendingUtc, fiscalHealth.CertificateExpiresUtc),
                await AuditHead.TextAsync(infra, ct)));
            return Ok.Value;
        }

        /// <summary>"1.4.0", without the commit suffix, so versions in the field group together.</summary>
        internal static string AppVersion =>
            ((Assembly.GetEntryAssembly() ?? typeof(SendHeartbeat).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
            .Split('+')[0];

        private static int UptimeMinutes()
        {
            using var process = Process.GetCurrentProcess();
            return (int)Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMinutes);
        }
    }
}

/// <summary>TK-249: asks the cloud for the store's current license (issued from its paid subscription, docs/cobro-paddle.md) and installs it when
/// it is newer. Nothing happens offline, without a connection, with an older cloud (404) or while the cloud has no license for the store.</summary>
internal static class PullLicense
{
    public const string Path = "/api/nodes/license";

    public static async Task<bool> RunAsync(CloudClient cloud, IStoreLicense license, CancellationToken ct)
    {
        if (await cloud.ConnectionAsync(ct) is not { } connection) return false;
        using var response = await cloud.SendAsync(connection, Path, new StringContent(""), ct);
        if (!response.IsSuccessStatusCode) return false;
        return (await response.Content.ReadFromJsonAsync<Answer>(ct))?.License is { Length: > 0 } jws && await license.InstallFromCloudAsync(jws, ct);
    }

    private sealed record Answer(string? License);
}

/// <summary>TK-254: asks the cloud which version this store may install on its channel (staged rollout from the platform console) and keeps the
/// answer for the server's update check and the registers. Offline, or with an older cloud (404), the last answer stays.</summary>
internal static class PullUpdatePolicy
{
    public const string Path = "/api/nodes/updates";

    public static async Task<bool> RunAsync(CloudClient cloud, IUpdatePolicy updates, IConfiguration config, CancellationToken ct)
    {
        if (await cloud.ConnectionAsync(ct) is not { } connection) return false;
        var policy = await updates.GetAsync(ct);
        var channel = policy.Channel ?? config["Updates:Channel"] ?? "stable";
        using var response = await cloud.SendAsync(connection, Path, JsonContent.Create(new { channel, version = SendHeartbeat.Handler.AppVersion }), ct);
        if (!response.IsSuccessStatusCode || await response.Content.ReadFromJsonAsync<Answer>(ct) is not { } a) return false;
        await updates.SetAllowedAsync(a.Managed ? a.AllowedVersion ?? "" : null, a.NotesUrl, ct);
        return true;
    }

    private sealed record Answer(bool Managed, string? AllowedVersion, string? NotesUrl);
}

/// <summary>Sends a heartbeat on start and every <c>Sync:HeartbeatMinutes</c> (240), unless <c>Sync:Enabled</c> is false (the test host),
/// and then pulls the license (<see cref="PullLicense"/>).</summary>
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
                if (await PullLicense.RunAsync(scope.ServiceProvider.GetRequiredService<CloudClient>(), scope.ServiceProvider.GetRequiredService<IStoreLicense>(), stoppingToken))
                    log.LogInformation("Installed the license the cloud issued");
                await PullUpdatePolicy.RunAsync(scope.ServiceProvider.GetRequiredService<CloudClient>(), scope.ServiceProvider.GetRequiredService<IUpdatePolicy>(),
                    config, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "The heartbeat failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
