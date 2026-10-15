using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.Identity.Contracts;
using Toki.Identity.Domain;
using Toki.Identity.Persistence;
using Toki.SharedKernel;

namespace Toki.Identity.Features;

/// <summary>First run: creates the administrator and pairs the main PC. Only works while there are no users.</summary>
internal static class Setup
{
    public sealed record Command(string AdministratorName, string AdministratorCode, string Pin, string DeviceName)
        : ICommand<Response>;

    public sealed record Response(Guid UserId, Guid DeviceId, string DeviceToken);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.AdministratorName).NotEmpty().MaximumLength(100);
            RuleFor(c => c.AdministratorCode).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).Must(Domain.Pin.IsValid).WithMessage("El PIN debe tener de 4 a 8 dígitos.");
            RuleFor(c => c.DeviceName).NotEmpty().MaximumLength(100);
        }
    }

    internal sealed class Handler(IdentityDbContext db, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Touching the Administrador role row first takes its write lock, so two setups cannot both see "no users".
            await db.Roles.Where(r => r.Id == Role.AdministratorId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, r => r.Name), ct);
            if (await db.Users.AnyAsync(ct)) return IdentityErrors.AlreadySetUp;

            var admin = new User { Id = Guid.CreateVersion7(), Name = c.AdministratorName.Trim(), Code = User.NormalizeCode(c.AdministratorCode), RoleId = Role.AdministratorId };
            admin.ChangePin(c.Pin);
            var token = Token.New();
            var device = new Device
            {
                Id = Guid.CreateVersion7(), Name = c.DeviceName.Trim(), TokenHash = Token.Hash(token), CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.Users.Add(admin);
            db.Devices.Add(device);
            outbox.Add(new UserProfileChangedV1(admin.Id, admin.Name, admin.IsActive));
            await audit.RecordAsync("identity.setup", "User", admin.Id, new { admin.Name, admin.Code, Device = device.Name }, ct);
            return new Response(admin.Id, device.Id, token);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/setup", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/identity/users/{r.UserId}", r)))
                .AllowAnonymous().WithTags("Identity");
    }
}
