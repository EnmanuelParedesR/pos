using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.CashRegister.Contracts;
using Toki.CashRegister.Domain;
using Toki.SharedKernel;

namespace Toki.CashRegister.Features;

internal static class Registers
{
    public sealed record Create(string Name) : ICommand<Response>;
    public sealed record Response(Guid Id);
    public sealed record Item(Guid Id, string Name, bool IsActive, Guid? OpenShiftId, string? Cashier);

    internal sealed class Validator : AbstractValidator<Create>
    {
        public Validator() => RuleFor(c => c.Name).NotEmpty().MaximumLength(60);
    }

    internal sealed class Handler(CashRegisterDbContext db, IOutbox outbox, IAuditLog audit) : ICommandHandler<Create, Response>
    {
        public async Task<Result<Response>> Handle(Create c, CancellationToken ct)
        {
            var register = new Register { Id = Guid.CreateVersion7(), Name = c.Name.Trim() };
            db.Registers.Add(register);
            outbox.Add(new RegisterChangedV1(register.Id, register.Name));
            await audit.RecordAsync("cash.create-register", "Register", register.Id, c, ct);
            return new Response(register.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/cash/registers", async (Create c, ICommandHandler<Create, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/cash/registers/{r.Id}", r)))
                .RequirePermission(Permissions.SettingsManage).WithTags("CashRegister");
            app.MapGet("/api/cash/registers", async (CashRegisterDbContext db, CancellationToken ct) =>
                    Results.Ok(await (from r in db.Registers
                                      join s in db.Shifts.Where(s => s.Status == ShiftStatus.Open) on r.Id equals s.RegisterId into open
                                      from s in open.DefaultIfEmpty()
                                      orderby r.Name
                                      select new Item(r.Id, r.Name, r.IsActive, s == null ? null : s.Id, s == null ? null : s.CashierName))
                        .AsNoTracking().ToListAsync(ct)))
                .RequireAuthorization().WithTags("CashRegister");
            // Moves the register to another device (replaced PC); null frees it so the next shift binds it again.
            app.MapPut("/api/cash/registers/{id:guid}/device", async (Guid id, AssignDevice body, ICommandHandler<AssignDevice, Response> h, CancellationToken ct) =>
                    (await h.Handle(body with { RegisterId = id }, ct)).ToHttp(Results.Ok))
                .RequirePermission(Permissions.SettingsManage).WithTags("CashRegister");
        }
    }

    public sealed record AssignDevice(Guid RegisterId, Guid? DeviceId) : ICommand<Response>;

    internal sealed class AssignDeviceHandler(CashRegisterDbContext db, IAuditLog audit) : ICommandHandler<AssignDevice, Response>
    {
        public async Task<Result<Response>> Handle(AssignDevice c, CancellationToken ct)
        {
            var register = await db.Registers.FirstOrDefaultAsync(x => x.Id == c.RegisterId, ct);
            if (register is null) return CashRegisterErrors.RegisterNotFound;
            register.DeviceId = c.DeviceId;
            await audit.RecordAsync("cash.assign-device", "Register", register.Id, c, ct);
            return new Response(register.Id);
        }
    }
}

/// <summary>A cashier opens a shift on a physical register with the opening float.</summary>
internal static class OpenShift
{
    public sealed record Command(Guid RegisterId, decimal OpeningFloat) : ICommand<Response>;
    public sealed record Response(Guid ShiftId);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.OpeningFloat).GreaterThanOrEqualTo(0).PrecisionScale(19, 2, true);
    }

    internal sealed class Handler(CashRegisterDbContext db, IUserContext user, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var register = await db.Registers.FirstOrDefaultAsync(x => x.Id == c.RegisterId && x.IsActive, ct);
            if (register is null) return CashRegisterErrors.RegisterNotFound;
            // The first shift binds the register to this device; afterwards only a supervisor may open it elsewhere.
            if (register.DeviceId is { } device && device != user.DeviceId && !user.Has(Permissions.RegisterApproveClose))
                return CashRegisterErrors.OtherDevice;
            register.DeviceId ??= user.DeviceId;
            if (await db.Shifts.AnyAsync(s => s.RegisterId == c.RegisterId && s.Status == ShiftStatus.Open, ct)) return CashRegisterErrors.RegisterInUse;
            if (await db.Shifts.AnyAsync(s => s.CashierId == user.UserId && s.Status == ShiftStatus.Open, ct)) return CashRegisterErrors.CashierHasShift;

            var shift = new Shift
            {
                Id = Guid.CreateVersion7(), RegisterId = c.RegisterId, CashierId = user.UserId, CashierName = user.Name,
                OpenedUtc = clock.GetUtcNow().UtcDateTime, OpeningFloat = c.OpeningFloat, Status = ShiftStatus.Open,
            };
            db.Shifts.Add(shift);
            outbox.Add(new ShiftOpenedV1(shift.Id, shift.RegisterId, shift.CashierId, shift.OpeningFloat));
            await audit.RecordAsync("cash.open-shift", "Shift", shift.Id, c, ct);
            return new Response(shift.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/cash/shifts", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/cash/shifts/{r.ShiftId}", r)))
                .RequirePermission(Permissions.RegisterOperate).WithTags("CashRegister");
    }
}

/// <summary>The caller's open shift (what the register shows after login). 404 when there is none.</summary>
internal static class CurrentShift
{
    public sealed record Response(Guid ShiftId, Guid RegisterId, string Register, decimal OpeningFloat, DateTime OpenedUtc);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/cash/shifts/current", async (IUserContext user, CashRegisterDbContext db, CancellationToken ct) =>
                {
                    var s = await (from shift in db.Shifts
                                   join register in db.Registers on shift.RegisterId equals register.Id
                                   where shift.CashierId == user.UserId && shift.Status == ShiftStatus.Open
                                   select new Response(shift.Id, register.Id, register.Name, shift.OpeningFloat, shift.OpenedUtc)).AsNoTracking().FirstOrDefaultAsync(ct);
                    return s is null ? EndpointExtensions.Problem(Error.NotFound("CashRegister.NoShift", "No tiene un turno abierto.")) : Results.Ok(s);
                })
                .RequirePermission(Permissions.RegisterOperate).WithTags("CashRegister");
    }
}

/// <summary>Cash in, withdrawal (cash drop) or petty expense. Withdrawals and expenses need a supervisor.</summary>
internal static class RecordMovement
{
    public enum Kind { CashIn, Withdrawal, Expense }

    /// <param name="MovementId">Generated by the register (GUID v7): a retry after a network drop returns the same movement instead of a second one.</param>
    public sealed record Command(Guid ShiftId, Guid MovementId, Kind MovementType, decimal Amount, MovementReason Reason, string? Note, Guid? SupervisorApprovalId)
        : ICommand<Response>, IRequiresApproval
    {
        public string Permission => MovementType == Kind.CashIn ? Permissions.RegisterOperate : Permissions.RegisterWithdraw;
        public Guid ResourceId => ShiftId;
    }

    public sealed record Response(Guid MovementId);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.MovementId).NotEmpty();
            RuleFor(c => c.MovementType).IsInEnum();
            RuleFor(c => c.Reason).IsInEnum();
            RuleFor(c => c.Amount).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Note).MaximumLength(200);
            RuleFor(c => c.Note).NotEmpty().When(c => c.Reason == MovementReason.Other).WithMessage("Explique el motivo.");
        }
    }

    internal sealed class Handler(CashRegisterDbContext db, CashRegisterOperations ops, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await SavedAsync(c, ct)) return new Response(c.MovementId);
            var shift = await ops.GetOpenShiftAsync(c.ShiftId, ct); // locks the shift: a concurrent retry waits here and then finds the movement
            if (shift.IsFailure) return shift.Error!;
            if (await SavedAsync(c, ct)) return new Response(c.MovementId);

            var (type, action) = c.MovementType switch
            {
                Kind.CashIn => (CashMovementType.CashIn, "cash.cash-in"),
                Kind.Withdrawal => (CashMovementType.Withdrawal, "cash.withdrawal"),
                _ => (CashMovementType.Expense, "cash.expense"),
            };
            var m = ops.Add(c.ShiftId, type, PaymentMethod.Cash, c.Amount, null, null, c.Reason, c.Note?.Trim(), c.MovementId);
            await audit.RecordAsync(action, "Shift", c.ShiftId, new { Movement = m.Id, c.Amount, c.Reason, c.Note }, ct);
            return new Response(m.Id);
        }

        // An id used on another shift is not found here; the unique index on Id refuses it when saving.
        private Task<bool> SavedAsync(Command c, CancellationToken ct) => db.Movements.AnyAsync(m => m.Id == c.MovementId && m.ShiftId == c.ShiftId, ct);
    }

    internal sealed record Body(Guid MovementId, Kind MovementType, decimal Amount, MovementReason Reason, string? Note, Guid? SupervisorApprovalId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/cash/shifts/{id:guid}/movements", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.MovementId, b.MovementType, b.Amount, b.Reason, b.Note, b.SupervisorApprovalId), ct)).ToHttp())
                .RequirePermission(Permissions.RegisterOperate).WithTags("CashRegister");
    }
}

/// <summary>Blind closing: the cashier counts bills and coins without seeing the expected cash; a supervisor approves.
/// Returns the Z report with the difference.</summary>
internal static class CloseShift
{
    public sealed record Command(Guid ShiftId, IReadOnlyList<DenominationCount> CashCount, Guid? SupervisorApprovalId) : ICommand<ZReport>, IRequiresApproval
    {
        public string Permission => Permissions.RegisterApproveClose;
        public Guid ResourceId => ShiftId;
    }

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.CashCount).NotNull();
            RuleForEach(c => c.CashCount).ChildRules(d =>
            {
                d.RuleFor(x => x.Denomination).Must(x => Denominations.Dop.Contains(x)).WithMessage("Esa denominación no existe.");
                d.RuleFor(x => x.Quantity).GreaterThanOrEqualTo(0);
            });
            RuleFor(c => c.CashCount).Must(c => c.Select(x => x.Denomination).Distinct().Count() == c.Count).WithMessage("Hay denominaciones repetidas.");
        }
    }

    internal sealed class Handler(CashRegisterDbContext db, InfraDbContext infra, ICashRegisterOperations operations, IUserContext user, IOutbox outbox, IAuditLog audit,
        TimeProvider clock) : ICommandHandler<Command, ZReport>
    {
        public async Task<Result<ZReport>> Handle(Command c, CancellationToken ct)
        {
            var open = await operations.GetOpenShiftAsync(c.ShiftId, ct); // locks the shift: in-flight sales finish first
            if (open.IsFailure) return open.Error!;

            var shift = await db.Shifts.SingleAsync(s => s.Id == c.ShiftId, ct);
            var movements = await db.Movements.Where(m => m.ShiftId == c.ShiftId).AsNoTracking().ToListAsync(ct);
            var count = c.CashCount.Where(x => x.Quantity > 0).OrderByDescending(x => x.Denomination).ToList();
            var lastZ = await db.Shifts.Where(s => s.RegisterId == shift.RegisterId && s.ZNumber != null).MaxAsync(s => s.ZNumber, ct) ?? 0;

            shift.Status = ShiftStatus.Closed;
            shift.ClosedUtc = clock.GetUtcNow().UtcDateTime;
            shift.ExpectedCash = ShiftBalance.ExpectedCash(shift.OpeningFloat, movements);
            shift.CountedCash = ShiftBalance.Counted(count);
            shift.Difference = shift.CountedCash - shift.ExpectedCash;
            shift.ClosingSupervisorId = user.SupervisorId ?? user.UserId;
            shift.CashCount = JsonSerializer.Serialize(count, Json.Options);
            shift.ZNumber = lastZ + 1;

            outbox.Add(new ShiftClosedV1(shift.Id, shift.RegisterId, shift.CashierId, shift.ZNumber.Value, shift.ExpectedCash.Value,
                shift.CountedCash.Value, shift.Difference.Value));
            await audit.RecordAsync("cash.close-shift", "Shift", shift.Id,
                new { shift.ZNumber, shift.ExpectedCash, shift.CountedCash, shift.Difference }, ct);
            shift.AuditChainHead = await AuditHead.TextAsync(infra, ct); // read after our own audit record: it includes the closing
            return ZReport.Build(shift, open.Value.Register, movements, count, shift.AuditChainHead);
        }
    }

    internal sealed record Body(IReadOnlyList<DenominationCount> CashCount, Guid? SupervisorApprovalId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/cash/shifts/{id:guid}/close", async (Guid id, Body b, ICommandHandler<Command, ZReport> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.CashCount, b.SupervisorApprovalId), ct)).ToHttp())
                .RequirePermission(Permissions.RegisterOperate).WithTags("CashRegister");
    }
}

/// <summary>Z report of a closed shift (its cashier or a supervisor), or X report of an open one (supervisor only: the count stays blind).</summary>
internal static class GetZReport
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/cash/shifts/{id:guid}/z", async (Guid id, IUserContext user, CashRegisterDbContext db, InfraDbContext infra, CancellationToken ct) =>
                {
                    var s = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (s is null) return EndpointExtensions.Problem(CashRegisterErrors.ShiftNotFound);
                    var supervisor = user.Has(Permissions.RegisterApproveClose) || user.Has(Permissions.ReportsView);
                    if (!supervisor && (s.CashierId != user.UserId || s.Status == ShiftStatus.Open))
                        return Results.Forbid();
                    var register = await db.Registers.Where(r => r.Id == s.RegisterId).Select(r => r.Name).SingleAsync(ct);
                    var movements = await db.Movements.Where(m => m.ShiftId == id).AsNoTracking().ToListAsync(ct);
                    var count = s.CashCount is null ? null : JsonSerializer.Deserialize<List<DenominationCount>>(s.CashCount, Json.Options);
                    return Results.Ok(ZReport.Build(s, register, movements, count, await AuditHead.TextAsync(infra, ct)));
                })
                .RequireAuthorization().WithTags("CashRegister");
    }
}

internal static class ListShifts
{
    public sealed record Item(Guid Id, string Register, string Cashier, DateTime OpenedUtc, DateTime? ClosedUtc, int? ZNumber,
        decimal OpeningFloat, decimal? ExpectedCash, decimal? CountedCash, decimal? Difference);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/cash/shifts", async (DateOnly from, DateOnly to, CashRegisterDbContext db, CancellationToken ct) =>
                {
                    var (f, t) = BusinessDay.UtcRange(from, to);
                    return Results.Ok(await (from s in db.Shifts
                                             join r in db.Registers on s.RegisterId equals r.Id
                                             where s.OpenedUtc >= f && s.OpenedUtc < t
                                             orderby s.OpenedUtc
                                             select new Item(s.Id, r.Name, s.CashierName, s.OpenedUtc, s.ClosedUtc, s.ZNumber, s.OpeningFloat,
                                                 s.ExpectedCash, s.CountedCash, s.Difference)).AsNoTracking().ToListAsync(ct));
                })
                .RequirePermission(Permissions.ReportsView).WithTags("CashRegister");
    }
}

internal static class ListDenominations
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/cash/denominations", () => Results.Ok(Denominations.Dop)).RequireAuthorization().WithTags("CashRegister");
    }
}
