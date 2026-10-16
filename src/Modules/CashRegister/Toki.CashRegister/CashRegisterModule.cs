using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.CashRegister.Contracts;
using Toki.CashRegister.Domain;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.CashRegister;

public static class CashRegisterModule
{
    public static IServiceCollection AddCashRegisterModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<CashRegisterDbContext>();
        services.AddScoped<CashRegisterOperations>();
        services.AddScoped<ICashRegisterOperations>(sp => sp.GetRequiredService<CashRegisterOperations>());
        services.AddScoped<ICashRegisterReports, CashRegisterReports>();
        services.AddSingleton<Features.CloseShift.BlindCloseAttempts>();
        services.AddScoped<ICloudSnapshot, RegisterSnapshot>();
        return services;
    }
}

internal sealed class RegisterSnapshot(CashRegisterDbContext db, IOutbox outbox) : ICloudSnapshot
{
    public async Task EnqueueAsync(CancellationToken ct)
    {
        foreach (var r in await db.Registers.AsNoTracking().ToListAsync(ct)) outbox.Add(new RegisterChangedV1(r.Id, r.Name));
    }
}

internal sealed class CashRegisterReports(CashRegisterDbContext db) : ICashRegisterReports
{
    public async Task<IReadOnlyList<ShiftSummary>> ShiftsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (f, t) = BusinessDay.UtcRange(from, to);
        return await (from s in db.Shifts
                      join r in db.Registers on s.RegisterId equals r.Id
                      where s.OpenedUtc >= f && s.OpenedUtc < t
                      orderby s.OpenedUtc
                      select new ShiftSummary(s.Id, r.Name, s.CashierName, s.OpenedUtc, s.ClosedUtc, s.ZNumber, s.OpeningFloat,
                          s.ExpectedCash, s.CountedCash, s.Difference)).AsNoTracking().ToListAsync(ct);
    }
}

internal sealed class CashRegisterDbContext(DbContextOptions<CashRegisterDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "cash";

    public DbSet<Register> Registers => Set<Register>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<CashMovement> Movements => Set<CashMovement>();

    public Task<Shift?> ShiftAsync(Guid id, CancellationToken ct) => Shifts.FirstOrDefaultAsync(s => s.Id == id, ct);
}

internal sealed class CashRegisterConfiguration : IEntityTypeConfiguration<Register>, IEntityTypeConfiguration<Shift>, IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<Register> b) => b.ToTable("Registers");

    public void Configure(EntityTypeBuilder<Shift> b)
    {
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(s => s.CashCount).HasMaxLength(ModuleDbContext.LongText);
        b.Property(s => s.Version).IsConcurrencyToken();
        // One open shift per register, enforced by the database too.
        b.HasIndex(s => s.RegisterId).IsUnique().HasFilter("[ClosedUtc] IS NULL").HasDatabaseName("IX_Shifts_OpenRegister");
        b.HasIndex(s => s.CashierId).IsUnique().HasFilter("[ClosedUtc] IS NULL").HasDatabaseName("IX_Shifts_OpenCashier");
        b.HasIndex(s => s.OpenedUtc);
    }

    public void Configure(EntityTypeBuilder<CashMovement> b)
    {
        b.ToTable("CashMovements");
        b.HasKey(m => m.Sequence);
        b.Property(m => m.Sequence).ValueGeneratedOnAdd();
        b.HasIndex(m => m.Id).IsUnique();
        b.HasIndex(m => m.ShiftId);
        b.HasIndex(m => m.ReferenceId);
        b.Property(m => m.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(m => m.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(m => m.Reason).HasConversion<string>().HasMaxLength(20);
        b.Ignore(m => m.IsInflow);
    }
}

internal sealed class CashRegisterOperations(CashRegisterDbContext db, IUserContext user, IOutbox outbox, TimeProvider clock) : ICashRegisterOperations
{
    /// <remarks>Also locks the shift row until the caller's transaction ends: a closing cannot count cash while a sale on this shift is still committing.</remarks>
    public async Task<Result<OpenShift>> GetOpenShiftAsync(Guid shiftId, CancellationToken ct)
    {
        await db.Shifts.Where(x => x.Id == shiftId && x.Status == ShiftStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), ct);
        var s = await (from shift in db.Shifts
                       join register in db.Registers on shift.RegisterId equals register.Id
                       where shift.Id == shiftId
                       select new { shift, register.Name, register.DeviceId }).AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null) return CashRegisterErrors.ShiftNotFound;
        if (s.shift.Status != ShiftStatus.Open) return CashRegisterErrors.ShiftClosed;
        var supervisor = user.Has(Permissions.RegisterApproveClose);
        if (s.shift.CashierId != user.UserId && !supervisor) return CashRegisterErrors.NotYourShift;
        if (s.DeviceId is { } device && device != user.DeviceId && !supervisor) return CashRegisterErrors.OtherDevice;
        return new OpenShift(s.shift.Id, s.shift.RegisterId, s.Name, s.shift.CashierId);
    }

    public Task RecordSalePaymentAsync(Guid shiftId, Guid saleId, IReadOnlyList<PaymentByMethod> payments, decimal rounding, CancellationToken ct)
    {
        foreach (var p in payments.Where(p => p.Amount > 0))
            Add(shiftId, CashMovementType.SalePayment, p.Method, p.Amount, saleId, p.Reference);
        if (rounding > 0) Add(shiftId, CashMovementType.Rounding, PaymentMethod.Cash, rounding, saleId, null);
        return Task.CompletedTask;
    }

    public async Task<Result> RecordRefundAsync(Guid shiftId, Guid referenceId, PaymentMethod method, decimal amount, decimal rounding, CancellationToken ct)
    {
        var shift = await GetOpenShiftAsync(shiftId, ct);
        if (shift.IsFailure) return shift.Error!;
        Add(shiftId, CashMovementType.Refund, method, amount, referenceId, null);
        if (rounding > 0) Add(shiftId, CashMovementType.Rounding, PaymentMethod.Cash, rounding, referenceId, null);
        return Result.Success();
    }

    public async Task<Result> RecordCustomerPaymentAsync(Guid shiftId, Guid customerPaymentId, PaymentMethod method, decimal amount, decimal rounding, CancellationToken ct)
    {
        var shift = await GetOpenShiftAsync(shiftId, ct);
        if (shift.IsFailure) return shift.Error!;
        Add(shiftId, CashMovementType.CustomerPayment, method, amount, customerPaymentId, null);
        if (rounding > 0) Add(shiftId, CashMovementType.Rounding, PaymentMethod.Cash, rounding, customerPaymentId, null);
        return Result.Success();
    }

    public async Task<Result> RecordOrderDepositAsync(Guid shiftId, Guid orderId, PaymentMethod method, decimal amount, CancellationToken ct)
    {
        var shift = await GetOpenShiftAsync(shiftId, ct);
        if (shift.IsFailure) return shift.Error!;
        Add(shiftId, CashMovementType.CustomerPayment, method, amount, orderId, ZReport.OrderDepositReference);
        return Result.Success();
    }

    public async Task<Result> RecordSupplierPaymentAsync(Guid shiftId, Guid supplierPaymentId, decimal amount, CancellationToken ct)
    {
        var shift = await GetOpenShiftAsync(shiftId, ct);
        if (shift.IsFailure) return shift.Error!;
        Add(shiftId, CashMovementType.Withdrawal, PaymentMethod.Cash, amount, supplierPaymentId, null, MovementReason.SupplierPayment);
        return Result.Success();
    }

    public async Task<Result> VoidMovementsAsync(Guid referenceId, CancellationToken ct)
    {
        var movements = await db.Movements.Where(m => m.ReferenceId == referenceId).AsNoTracking().ToListAsync(ct);
        if (movements.Count == 0) return Result.Success();
        var shiftId = movements[0].ShiftId;
        // The update locks the shift: a closing cannot count the drawer while the reversal commits.
        if (await db.Shifts.Where(s => s.Id == shiftId && s.Status == ShiftStatus.Open)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), ct) == 0)
            return CashRegisterErrors.ShiftClosed;
        foreach (var m in movements)
        {
            switch (m.Type)
            {
                case CashMovementType.SalePayment: Add(shiftId, CashMovementType.SalePaymentVoid, m.Method, m.Amount, referenceId, m.Reference); break;
                case CashMovementType.Rounding: Add(shiftId, CashMovementType.SalePaymentVoid, m.Method, m.Amount, referenceId, ZReport.RoundingReference); break;
                case CashMovementType.CustomerPayment: Add(shiftId, CashMovementType.CustomerPaymentVoid, m.Method, m.Amount, referenceId, m.Reference); break;
                case CashMovementType.Withdrawal: Add(shiftId, CashMovementType.CashIn, m.Method, m.Amount, referenceId, null, m.Reason, "Anulación"); break;
            }
        }
        return Result.Success();
    }

    public async Task<decimal> ExpectedCashAsync(Guid shiftId, CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking().SingleAsync(s => s.Id == shiftId, ct);
        var movements = await db.Movements.Where(m => m.ShiftId == shiftId).AsNoTracking().ToListAsync(ct);
        movements.AddRange(db.Movements.Local.Where(m => m.ShiftId == shiftId && db.Entry(m).State == EntityState.Added));
        return ShiftBalance.ExpectedCash(shift.OpeningFloat, movements);
    }

    public CashMovement Add(Guid shiftId, CashMovementType type, PaymentMethod method, decimal amount, Guid? referenceId, string? reference,
        MovementReason? reason = null, string? note = null, Guid? id = null)
    {
        var m = new CashMovement
        {
            Id = id ?? Guid.CreateVersion7(), ShiftId = shiftId, Type = type, Method = method, Amount = amount, ReferenceId = referenceId,
            Reference = reference, Reason = reason, Note = note, UserId = user.UserId == Guid.Empty ? null : user.UserId,
            SupervisorId = user.SupervisorId, DateUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Movements.Add(m);
        outbox.Add(new CashMovementRecordedV1(shiftId, type.ToString(), method, amount, referenceId));
        return m;
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class CashRegisterDesignTimeFactory : Toki.BuildingBlocks.Persistence.DesignTimeFactory<CashRegisterDbContext>;
