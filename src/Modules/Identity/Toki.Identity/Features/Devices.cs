using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Identity.Domain;
using Toki.Identity.Persistence;
using Toki.SharedKernel;

namespace Toki.Identity.Features;

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

    internal sealed class Handler(IdentityDbContext db, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // shortcut: 8 random digits valid 10 minutes, no attempt counter; fine on a shop LAN (what the code unlocks is one client certificate, TK-091).
            var code = Token.ShortCode() + Token.ShortCode()[..2];
            var expires = clock.GetUtcNow().UtcDateTime + Validity;
            db.PairingCodes.Add(new PairingCode
            {
                Id = Guid.CreateVersion7(), DeviceName = c.DeviceName.Trim(), CodeHash = Token.Hash(code), ExpiresUtc = expires,
            });
            return Task.FromResult<Result<Response>>(new Response(code, expires));
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
    public sealed record Command(string Code, string? CertificateRequest = null) : ICommand<Response>;
    /// <param name="Token">The device secret, only when no CSR was sent.</param>
    public sealed record Response(Guid DeviceId, string Name, string? Token, string? Certificate, string? StoreCa);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
            RuleFor(c => c.CertificateRequest).ValidCertificateRequest();
        }
    }

    internal sealed class Handler(IdentityDbContext db, IFeatureService license, IAuditLog audit, StoreCertificateAuthority ca, TimeProvider clock)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var hash = Token.Hash(c.Code.Trim());
            var valid = db.PairingCodes.Where(x => x.CodeHash == hash && x.UsedUtc == null && x.ExpiresUtc > now);
            var code = await valid.AsNoTracking().FirstOrDefaultAsync(ct);
            // Claim it with a conditional update: two registers racing with the same code cannot both pair.
            if (code is null || await valid.ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedUtc, now), ct) == 0)
                return IdentityErrors.InvalidPairingCode;
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
            app.MapPost("/api/identity/devices/pair", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
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
