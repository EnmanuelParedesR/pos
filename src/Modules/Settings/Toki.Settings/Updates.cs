using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Settings.Contracts;
using Toki.SharedKernel;

namespace Toki.Settings;

/// <summary>TK-254: the version each register runs and the one it downloaded (applied when it restarts), as it last reported it.</summary>
internal sealed class RegisterVersion
{
    public Guid DeviceId { get; set; }
    public string Version { get; set; } = "";
    public string? PendingVersion { get; set; }
    public DateTime ReportedUtc { get; set; }
    /// <summary>The owner asked this register to restart and apply <see cref="PendingVersion"/>; cleared once it reports without one.</summary>
    public DateTime? RestartRequestedUtc { get; set; }
}

internal sealed class RegisterVersionConfiguration : IEntityTypeConfiguration<RegisterVersion>
{
    public void Configure(EntityTypeBuilder<RegisterVersion> b)
    {
        b.ToTable("RegisterVersions");
        b.HasKey(r => r.DeviceId);
        b.Property(r => r.DeviceId).ValueGeneratedNever();
        b.Property(r => r.Version).HasMaxLength(20);
        b.Property(r => r.PendingVersion).HasMaxLength(20);
    }
}

/// <summary>TK-254: what the server's update check, the cloud channel (Sync) and the admin page share. Kept on the Business row.</summary>
internal sealed class UpdatePolicyStore(SettingsDbContext db) : IUpdatePolicy
{
    public async Task<UpdatePolicy> GetAsync(CancellationToken ct)
    {
        var b = await db.Business.AsNoTracking().SingleAsync(ct);
        return new UpdatePolicy(b.UpdateChannel, b.UpdateOutsideHours ?? true, b.UpdateRequestedUtc, b.UpdateAllowedVersion);
    }

    public Task SetAvailableAsync(string? version, CancellationToken ct) =>
        db.Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdateAvailableVersion, version), ct);

    public Task SetAllowedAsync(string? version, string? notesUrl, CancellationToken ct) =>
        db.Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdateAllowedVersion, version).SetProperty(b => b.UpdateNotesUrl, notesUrl), ct);

    public Task AppliedAsync(CancellationToken ct) =>
        db.Business.Where(b => b.UpdateRequestedUtc != null).ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdateRequestedUtc, (DateTime?)null), ct);
}

internal static class UpdateChannels
{
    public const string Pilot = "pilot";
    public const string Stable = "stable";
    public static bool IsValid(string? c) => c is Pilot or Stable;
}

/// <summary>TK-254, Configuración → Actualizaciones: versions of the server and of each register, the channel and when updates apply.</summary>
internal static class GetUpdates
{
    public sealed record RegisterRow(Guid DeviceId, string Version, string? PendingVersion, DateTime ReportedUtc, bool RestartRequested);

    /// <param name="Configured">This server has a release feed (Updates:FeedUrl); without one it never looks for updates.</param>
    /// <param name="AllowedVersion">What the cloud lets this store install (staged rollout); null when the cloud does not manage it, "" to wait.</param>
    public sealed record Response(bool Configured, string ServerVersion, string? AvailableVersion, string? AllowedVersion, string Channel, bool OutsideHours,
        DateTime? RequestedUtc, string? NotesUrl, IReadOnlyList<RegisterRow> Registers);

    /// <summary>"1.4.0", without the commit suffix (the same text the heartbeat sends).</summary>
    public static string ServerVersion =>
        ((Assembly.GetEntryAssembly() ?? typeof(GetUpdates).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];

    public static string ChannelOf(Business b, IConfiguration config) => b.UpdateChannel ?? (UpdateChannels.IsValid(config["Updates:Channel"]) ? config["Updates:Channel"]! : UpdateChannels.Stable);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/settings/updates", async (SettingsDbContext db, IConfiguration config, CancellationToken ct) =>
                {
                    var b = await db.Business.AsNoTracking().SingleAsync(ct);
                    var registers = await db.RegisterVersions.AsNoTracking().OrderByDescending(r => r.ReportedUtc)
                        .Select(r => new RegisterRow(r.DeviceId, r.Version, r.PendingVersion, r.ReportedUtc, r.RestartRequestedUtc != null)).ToListAsync(ct);
                    return Results.Ok(new Response(!string.IsNullOrWhiteSpace(config["Updates:FeedUrl"]), ServerVersion, b.UpdateAvailableVersion, b.UpdateAllowedVersion,
                        ChannelOf(b, config), b.UpdateOutsideHours ?? true, b.UpdateRequestedUtc, b.UpdateNotesUrl, registers));
                })
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
    }
}

/// <summary>TK-254: the channel (pilot gets a release first) and whether updates install on their own outside business hours.</summary>
internal static class SaveUpdatePolicy
{
    public sealed record Command(string Channel, bool OutsideHours) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Channel).Must(UpdateChannels.IsValid).WithMessage("Elija el canal piloto o estable.");
    }

    internal sealed class Handler(SettingsDbContext db, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var b = await db.Business.SingleAsync(ct);
            (b.UpdateChannel, b.UpdateOutsideHours) = (c.Channel, c.OutsideHours);
            await audit.RecordAsync("settings.updates", "Business", Guid.Empty, c, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/settings/updates", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
    }
}

/// <summary>TK-254 "Actualizar ahora": the server installs the update as soon as no shift is open, or after closing time
/// (the server's update check and the scheduled task do it; a check runs within a minute).</summary>
internal static class RequestServerUpdate
{
    public sealed record Command : ICommand<Ok>;

    internal sealed class Handler(SettingsDbContext db, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var b = await db.Business.SingleAsync(ct);
            if (b.UpdateAvailableVersion is null)
                return Error.Conflict("Settings.NoUpdate", "No hay una actualización disponible para el servidor.");
            b.UpdateRequestedUtc = clock.GetUtcNow().UtcDateTime;
            await audit.RecordAsync("settings.updates.apply-now", "Business", Guid.Empty, new { Version = b.UpdateAvailableVersion }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/settings/updates/apply-now", async (ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
    }
}

/// <summary>TK-254: the owner asks a register to restart and apply the version it downloaded. It does at its next report, with no sale in progress.</summary>
internal static class RequestRegisterRestart
{
    public sealed record Command(Guid DeviceId) : ICommand<Ok>;

    internal sealed class Handler(SettingsDbContext db, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var r = await db.RegisterVersions.FindAsync([c.DeviceId], ct);
            if (r?.PendingVersion is null) return Error.Conflict("Settings.NoPendingUpdate", "Esa caja no tiene una actualización descargada.");
            r.RestartRequestedUtc = clock.GetUtcNow().UtcDateTime;
            await audit.RecordAsync("settings.updates.register-restart", "Device", c.DeviceId, new { r.Version, r.PendingVersion }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/settings/updates/registers/{deviceId:guid}/restart", async (Guid deviceId, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(deviceId), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
    }
}

/// <summary>TK-254: each register says which version it runs. The answer brings the version it gets once Toki is closed (the setup's SYSTEM task
/// installs it from the feed: the server's own version or the one the server is about to install, whichever is newer), the store's channel, the
/// highest version the cloud allows and whether the owner asked it to close and update. Any signed-in register (the device comes from its session).</summary>
internal static class ReportRegisterVersion
{
    public sealed record Request(string? Version);
    public sealed record Response(string Channel, string? AllowedVersion, string? PendingVersion, bool RestartRequested);

    private static string? Clean(string? v) => v?.Trim() is { Length: > 0 and <= 20 } t ? t : null;

    /// <summary>The newer of the server's version and the one it will install, if it is newer than the register's; null when the register is up to date.</summary>
    public static string? Pending(string register, string server, string? available)
    {
        static Version? V(string? s) => Version.TryParse(s, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
        if (V(register) is not { } current) return null;
        var target = new[] { V(server), V(available) }.Max();
        return target is not null && target > current ? target.ToString(3) : null;
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/settings/updates/register", async (Request r, SettingsDbContext db, IUserContext me, IConfiguration config, TimeProvider clock, CancellationToken ct) =>
                {
                    if (me.DeviceId is not { } deviceId || Clean(r.Version) is not { } version) return Results.BadRequest();
                    var b = await db.Business.AsNoTracking().SingleAsync(ct);
                    var row = await db.RegisterVersions.FindAsync([deviceId], ct);
                    if (row is null) db.RegisterVersions.Add(row = new RegisterVersion { DeviceId = deviceId });
                    (row.Version, row.PendingVersion, row.ReportedUtc) =
                        (version, Pending(version, GetUpdates.ServerVersion, b.UpdateAvailableVersion), clock.GetUtcNow().UtcDateTime);
                    if (row.PendingVersion is null) row.RestartRequestedUtc = null; // updated (or nothing to update)
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new Response(GetUpdates.ChannelOf(b, config), b.UpdateAllowedVersion, row.PendingVersion, row.RestartRequestedUtc is not null));
                })
                .RequireAuthorization().WithTags("Settings");
    }
}
