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
            // shortcut: 8 random digits valid 10 minutes, no attempt counter; fine on a shop LAN, replace with mTLS pairing (TK-091).
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

/// <summary>The new register sends the code and receives its device secret.</summary>
internal static class PairDevice
{
    public sealed record Command(string Code) : ICommand<Response>;
    public sealed record Response(Guid DeviceId, string Name, string Token);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
    }

    internal sealed class Handler(IdentityDbContext db, IFeatureService license, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
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

            var token = Token.New();
            var device = new Device { Id = Guid.CreateVersion7(), Name = code.DeviceName, TokenHash = Token.Hash(token), CreatedUtc = now };
            db.Devices.Add(device);
            await audit.RecordAsync("identity.pair", "Device", device.Id, new { device.Name }, ct);
            return new Response(device.Id, device.Name, token);
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

/// <summary>Revoking is immediate: its sessions close and its secret stops working.</summary>
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
