using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Identity.Domain;
using Abastalia.Identity.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Identity.Features;

/// <summary>An administrator opens a 10-minute pairing window for a new register.</summary>
internal static class CreatePairingCode
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

    public sealed record Command(string DeviceName) : ICommand<Response>;
    public sealed record Response(string Code, DateTime ExpiresUtc);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.DeviceName).NotEmpty().MaximumLength(100);
    }

    internal sealed class Handler(IdentityDbContext db, AttemptLimiter limiter, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // 8 random digits valid 10 minutes. R11 C12: one code at a time (a new one ends the others) and few wrong tries (PairDevice).
            var code = Token.ShortCode() + Token.ShortCode()[..2];
            var now = clock.GetUtcNow().UtcDateTime;
            await db.PairingCodes.Where(x => x.UsedUtc == null && x.ExpiresUtc > now).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresUtc, now), ct);
            limiter.Clear(PairDevice.WindowKey);
            db.PairingCodes.Add(new PairingCode
            {
                Id = Guid.CreateVersion7(), DeviceName = c.DeviceName.Trim(), CodeHash = Token.Hash(code), ExpiresUtc = now + Validity,
            });
            return new Response(code, now + Validity);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/devices/pairing-codes", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .RequirePermission(Permissions.DevicesManage).WithTags("Identity");
    }
}

/// <summary>What a newly paired device proves itself with (ADR-0009): a client certificate issued by the store CA when it sent a CSR (TK-091),
/// otherwise a secret (development over http). Its secret is then never revealed: only its hash is kept, to fill the column.</summary>
internal static class DeviceCredentials
{
    public const int MaxRequestLength = 4000;

    public static (string? Token, string? Certificate, string? StoreCa) Issue(Device device, string? certificateRequest, StoreCertificateAuthority ca)
    {
        var token = Token.New();
        device.TokenHash = Token.Hash(token);
        if (string.IsNullOrWhiteSpace(certificateRequest)) return (token, null, null);
        var (pem, thumbprint) = ca.IssueDeviceCertificate(certificateRequest, device.Id);
        device.CertificateThumbprint = thumbprint;
        return (null, pem, ca.CertificatePem);
    }

    public static IRuleBuilderOptions<T, string?> ValidCertificateRequest<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(MaxRequestLength).Must(r => string.IsNullOrWhiteSpace(r) || StoreCertificateAuthority.IsValidRequest(r))
            .WithMessage("La solicitud de certificado de este equipo no es válida.");
}

/// <summary>The new register sends the code (and its CSR) and receives its client certificate and the store CA to pin.</summary>
internal static class PairDevice
{
    /// <param name="CertificateRequest">PEM CSR of the key the register just made (TK-091); none over plain http (development).</param>
    public sealed record Command(string Code, string? CertificateRequest = null) : ICommand<Response>
    {
        /// <summary>Where the request came from, set by the endpoint (R11 C12: wrong codes are limited per address).</summary>
        public string? Address { get; init; }
    }
    /// <param name="Token">The device secret, only when no CSR was sent.</param>
    public sealed record Response(Guid DeviceId, string Name, string? Token, string? Certificate, string? StoreCa);

    /// <summary>R11 C12: after this many wrong codes the open code stops working (a new one is needed); per address, a pause.</summary>
    public const int WrongCodesPerWindow = 5, WrongCodesPerAddress = 10;
    public const string WindowKey = "pairing";

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
            RuleFor(c => c.CertificateRequest).ValidCertificateRequest();
        }
    }

    internal sealed class Handler(IdentityDbContext db, IFeatureService license, IAuditLog audit, StoreCertificateAuthority ca, AttemptLimiter limiter,
        TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var address = $"pairing:{c.Address}";
            if (limiter.IsBlocked(WindowKey, WrongCodesPerWindow) || limiter.IsBlocked(address, WrongCodesPerAddress)) return IdentityErrors.TooManyPairingAttempts;
            var now = clock.GetUtcNow().UtcDateTime;
            var hash = Token.Hash(c.Code.Trim());
            var valid = db.PairingCodes.Where(x => x.CodeHash == hash && x.UsedUtc == null && x.ExpiresUtc > now);
            var code = await valid.AsNoTracking().FirstOrDefaultAsync(ct);
            // Claim it with a conditional update: two registers racing with the same code cannot both pair.
            if (code is null || await valid.ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedUtc, now), ct) == 0)
            {
                limiter.Fail(address);
                limiter.Fail(WindowKey);
                return IdentityErrors.InvalidPairingCode;
            }
            // The main PC (paired at setup) is not a register.
            // shortcut: two pairings at the same instant may pass the limit by one; pairing is rare and done by the owner.
            if (await db.Devices.CountAsync(d => !d.IsRevoked, ct) - 1 >= license.LimitOf(Limit.Registers))
                return Error.Forbidden("Identity.RegisterLimitReached", $"La licencia permite {license.LimitOf(Limit.Registers)} cajas. Revoque una que no use o amplíe el plan.");

            var device = new Device { Id = Guid.CreateVersion7(), Name = code.DeviceName, CreatedUtc = now };
            var (token, certificate, storeCa) = DeviceCredentials.Issue(device, c.CertificateRequest, ca);
            db.Devices.Add(device);
            await audit.RecordAsync("identity.pair", "Device", device.Id, new { device.Name, device.CertificateThumbprint }, ct);
            return new Response(device.Id, device.Name, token, certificate, storeCa);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/devices/pair", async (Command c, HttpContext http, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c with { Address = http.Connection.RemoteIpAddress?.ToString() }, ct)).ToHttp())
                .AllowAnonymous().WithTags("Identity");
    }
}

/// <summary>R11 C5: a register renews its client certificate (one year) before it expires, proving itself with the current one over mTLS.
/// The certificate it renews stays accepted until it expires, in case the answer never reaches the register.</summary>
internal static class RenewDeviceCertificate
{
    public sealed record Command(string CertificateRequest) : ICommand<Response>
    {
        /// <summary>The paired device and the certificate it connected with, set by the endpoint.</summary>
        public Guid? DeviceId { get; init; }
        public string? Presented { get; init; }
    }
    public sealed record Response(string Certificate);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.CertificateRequest).NotEmpty().ValidCertificateRequest();
    }

    internal sealed class Handler(IdentityDbContext db, IAuditLog audit, StoreCertificateAuthority ca) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (c.DeviceId is not { } id || c.Presented is null) return IdentityErrors.DeviceNotPaired;
            var device = await db.Devices.SingleAsync(d => d.Id == id, ct);
            var (pem, thumbprint) = ca.IssueDeviceCertificate(c.CertificateRequest, id);
            (device.PreviousCertificateThumbprint, device.CertificateThumbprint) = (c.Presented, thumbprint);
            await audit.RecordAsync("identity.renew-certificate", "Device", id, new { device.Name, device.CertificateThumbprint }, ct);
            return new Response(pem);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/devices/current/certificate", async (Command c, HttpContext http, IdentityDbContext db, IConfiguration config,
                    ICommandHandler<Command, Response> h, CancellationToken ct) =>
                {
                    // Only a client certificate renews one: never a device secret.
                    if (http.Connection.ClientCertificate is not { } certificate) return EndpointExtensions.Problem(IdentityErrors.DeviceNotPaired);
                    var command = c with { DeviceId = await db.IdOfAsync(http, config, ct), Presented = StoreCertificateAuthority.Thumbprint(certificate) };
                    return (await h.Handle(command, ct)).ToHttp();
                })
                .AllowAnonymous().WithTags("Identity");
    }
}

internal static class ListDevices
{
    public sealed record Item(Guid Id, string Name, bool IsRevoked, DateTime CreatedUtc);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identity/devices", async (IdentityDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Devices.OrderBy(d => d.Name)
                        .Select(d => new Item(d.Id, d.Name, d.IsRevoked, d.CreatedUtc)).AsNoTracking().ToListAsync(ct)))
                .RequirePermission(Permissions.DevicesManage).WithTags("Identity");
    }
}

/// <summary>Revoking is immediate: its sessions close and its certificate (or secret) stops working.</summary>
internal static class RevokeDevice
{
    public sealed record Command(Guid Id) : ICommand<Ok>;

    internal sealed class Handler(IdentityDbContext db, IAuditLog audit, IUserContext me, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (c.Id == me.DeviceId) return Error.Validation("Identity.CannotRevokeOwnDevice", "No puede revocar el equipo que está usando.");
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == c.Id, ct);
            if (device is null) return IdentityErrors.DeviceNotFound;
            device.IsRevoked = true;
            var now = clock.GetUtcNow().UtcDateTime;
            await db.Sessions.Where(s => s.DeviceId == c.Id && s.ClosedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedUtc, now), ct);
            await audit.RecordAsync("identity.revoke", "Device", device.Id, new { device.Name }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/devices/{id:guid}/revoke", async (Guid id, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.DevicesManage).WithTags("Identity");
    }
}

/// <summary>TK-190: the register says whether it has a scale and a printer (its settings live on the PC, not here), at sign-in and when they
/// change, so the heartbeat can count them. Only the kind: never a port or a printer name. Any signed-in session of the device may report.</summary>
internal static class ReportPeripherals
{
    /// <param name="ScaleProtocol">Null without a scale.</param>
    /// <param name="PrinterWidth">58 or 80; null without a printer.</param>
    public sealed record Request(string? ScaleProtocol, int? PrinterWidth);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/identity/devices/current/peripherals", async (Request r, IdentityDbContext db, IUserContext me, TimeProvider clock, CancellationToken ct) =>
                {
                    if (r.ScaleProtocol is { Length: > 20 } || r.PrinterWidth is not (null or 58 or 80))
                        return EndpointExtensions.Problem(Error.Validation("Identity.InvalidPeripherals", "Los equipos de la caja no son válidos."));
                    var scale = string.IsNullOrWhiteSpace(r.ScaleProtocol) ? null : r.ScaleProtocol.Trim();
                    var updated = await db.Devices.Where(d => d.Id == me.DeviceId && !d.IsRevoked).ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.PeripheralsReportedUtc, clock.GetUtcNow().UtcDateTime)
                        .SetProperty(d => d.ScaleProtocol, scale)
                        .SetProperty(d => d.PrinterWidth, r.PrinterWidth), ct);
                    return updated == 0 ? EndpointExtensions.Problem(IdentityErrors.DeviceNotFound) : Results.NoContent();
                })
                .WithTags("Identity");
    }
}
