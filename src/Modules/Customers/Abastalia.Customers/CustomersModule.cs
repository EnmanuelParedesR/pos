using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Customers.Contracts;
using Abastalia.Customers.Domain;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Customers;

public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<CustomersDbContext>();
        services.AddScoped<ICustomerAccounts, CustomerAccounts>();
        services.AddScoped<ICustomerReports, Features.CustomerReports>();
        services.AddScoped<ICloudSnapshot, CustomerSnapshot>();
        return services;
    }
}

internal sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "customers";

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<OnAccountCharge> Charges => Set<OnAccountCharge>();
    public DbSet<CustomerPayment> Payments => Set<CustomerPayment>();
    public DbSet<CustomerPaymentApplication> Applications => Set<CustomerPaymentApplication>();
    public DbSet<CreditBalanceMovement> BalanceMovements => Set<CreditBalanceMovement>();

    /// <summary>Locks the customer row until commit: limit checks, payments and balance use run one at a time per customer.</summary>
    public Task<int> LockAsync(Guid customerId, CancellationToken ct) =>
        Customers.Where(c => c.Id == customerId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Version, c => c.Version + 1), ct);

    /// <summary>Open credit charges with their balance, oldest first: of one customer, or of all (aging). Paid charges stay in the database.</summary>
    public async Task<List<(OnAccountCharge Charge, decimal Balance)>> OpenChargesAsync(Guid? customerId, CancellationToken ct)
    {
        var open = await (from c in Charges
                          where !c.IsVoided && (customerId == null || c.CustomerId == customerId)
                          let paid = (from a in Applications
                                      join p in Payments on a.CustomerPaymentId equals p.Id
                                      where a.ChargeId == c.Id && !p.IsVoided
                                      select (decimal?)a.Amount).Sum() ?? 0
                          where c.Amount > paid
                          select new { c, Balance = c.Amount - paid }).AsNoTracking().ToListAsync(ct);
        return open.OrderBy(x => x.c.DateUtc).Select(x => (x.c, x.Balance)).ToList();
    }

    /// <summary>What live payments have paid on one charge.</summary>
    public Task<decimal> PaidAsync(Guid chargeId, CancellationToken ct) =>
        (from a in Applications join p in Payments on a.CustomerPaymentId equals p.Id where a.ChargeId == chargeId && !p.IsVoided select a.Amount).SumAsync(ct);

    public Task<decimal> CreditBalanceAsync(Guid customerId, CancellationToken ct) =>
        BalanceMovements.Where(m => m.CustomerId == customerId).SumAsync(m => m.Amount, ct);

    /// <summary>TK-174: saves this module's changes so far (inside the command's transaction, like a sale's receipt number) and writes
    /// the customer's account as it now stands to the outbox. Call it after every change to that customer in the command.</summary>
    public async Task PublishBalanceAsync(IOutbox outbox, Guid customerId, CancellationToken ct)
    {
        await SaveChangesAsync(ct);
        var name = await Customers.Where(c => c.Id == customerId).Select(c => c.Name).SingleAsync(ct);
        var debt = (await OpenChargesAsync(customerId, ct)).Sum(x => x.Balance);
        outbox.Add(new CustomerBalanceChangedV1(customerId, name, debt, await CreditBalanceAsync(customerId, ct)));
    }
}

internal sealed class CustomersConfiguration :
    IEntityTypeConfiguration<Customer>, IEntityTypeConfiguration<OnAccountCharge>, IEntityTypeConfiguration<CustomerPayment>,
    IEntityTypeConfiguration<CustomerPaymentApplication>, IEntityTypeConfiguration<CreditBalanceMovement>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.Property(c => c.Name).HasMaxLength(150);
        b.HasIndex(c => c.Name);
        b.Property(c => c.TaxId).HasMaxLength(11);
        b.HasIndex(c => c.TaxId).IsUnique().HasFilter("[TaxId] IS NOT NULL");
        b.Property(c => c.Phone).HasMaxLength(30);
        b.Property(c => c.PriceLevel).HasConversion<string>().HasMaxLength(10);
    }

    public void Configure(EntityTypeBuilder<OnAccountCharge> b)
    {
        b.ToTable("OnAccountCharges");
        b.HasIndex(c => c.CustomerId);
        b.HasIndex(c => c.SaleId);
        b.Property(c => c.Document).HasMaxLength(13);
    }

    public void Configure(EntityTypeBuilder<CustomerPayment> b)
    {
        b.ToTable("CustomerPayments");
        b.HasIndex(p => p.CustomerId);
        b.Property(p => p.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.Reference).HasMaxLength(60);
        b.HasMany(p => p.Applications).WithOne().HasForeignKey(x => x.CustomerPaymentId);
    }

    public void Configure(EntityTypeBuilder<CustomerPaymentApplication> b)
    {
        b.ToTable("CustomerPaymentApplications");
        b.HasKey(a => new { a.CustomerPaymentId, a.ChargeId });
        b.HasIndex(a => a.ChargeId);
    }

    public void Configure(EntityTypeBuilder<CreditBalanceMovement> b)
    {
        b.ToTable("CreditBalanceMovements");
        b.HasKey(m => m.Sequence);
        b.Property(m => m.Sequence).ValueGeneratedOnAdd();
        b.HasIndex(m => m.CustomerId);
        b.HasIndex(m => m.ReferenceId);
        b.Property(m => m.Type).HasConversion<string>().HasMaxLength(20);
    }
}

internal sealed class CustomerAccounts(CustomersDbContext db, IUserContext user, IOutbox outbox, TimeProvider clock) : ICustomerAccounts
{
    public async Task<CustomerForSale?> GetAsync(Guid customerId, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId, ct);
        if (c is null) return null;
        var debt = (await db.OpenChargesAsync(customerId, ct)).Sum(x => x.Balance);
        return new CustomerForSale(c.Id, c.Name, c.TaxId, c.FiscalCredit, c.PriceLevel, c.CreditLimit, debt, await db.CreditBalanceAsync(customerId, ct), c.IsActive);
    }

    public async Task<Result> ChargeOnAccountAsync(Guid customerId, Guid saleId, string document, decimal amount, bool allowOverLimit, CancellationToken ct)
    {
        if (await db.LockAsync(customerId, ct) == 0) return CustomersErrors.CustomerNotFound;
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId, ct);
        if (!customer.IsActive) return CustomersErrors.CustomerNotFound;
        var debt = (await db.OpenChargesAsync(customerId, ct)).Sum(x => x.Balance);
        if (!allowOverLimit && debt + amount > customer.CreditLimit) return CustomersErrors.CreditLimitExceeded(Math.Max(0, customer.CreditLimit - debt));

        db.Charges.Add(new OnAccountCharge
        {
            Id = Guid.CreateVersion7(), CustomerId = customerId, SaleId = saleId, Document = document, DateUtc = clock.GetUtcNow().UtcDateTime, Amount = amount,
        });
        outbox.Add(new OnAccountChargeRecordedV1(customerId, saleId, amount));
        await db.PublishBalanceAsync(outbox, customerId, ct);
        return Result.Success();
    }

    public async Task<Result> UseCreditBalanceAsync(Guid customerId, Guid saleId, decimal amount, decimal reserved, CancellationToken ct)
    {
        if (await db.LockAsync(customerId, ct) == 0) return CustomersErrors.CustomerNotFound;
        if (await db.CreditBalanceAsync(customerId, ct) - reserved < amount) return CustomersErrors.InsufficientCreditBalance;
        AddBalance(customerId, CreditBalanceMovementType.UsedInSale, -amount, saleId);
        await db.PublishBalanceAsync(outbox, customerId, ct);
        return Result.Success();
    }

    public async Task VoidSaleAsync(Guid saleId, CancellationToken ct)
    {
        var customerId = await db.Charges.Where(c => c.SaleId == saleId).Select(c => (Guid?)c.CustomerId)
            .Concat(db.BalanceMovements.Where(m => m.ReferenceId == saleId).Select(m => (Guid?)m.CustomerId)).FirstOrDefaultAsync(ct);
        if (customerId is null) return;
        await db.LockAsync(customerId.Value, ct);

        var charge = await db.Charges.FirstOrDefaultAsync(c => c.SaleId == saleId && !c.IsVoided, ct);
        if (charge is not null)
        {
            charge.PaidAtVoid = await db.PaidAsync(charge.Id, ct);
            charge.IsVoided = true;
            charge.VoidedUtc = clock.GetUtcNow().UtcDateTime;
            // What was already paid on the voided sale pays the customer's other debts first; only the rest becomes credit balance.
            var rest = charge.PaidAtVoid - ApplyPayment(customerId.Value, CustomerPaymentSource.SaleVoid, saleId, charge.PaidAtVoid,
                (await db.OpenChargesAsync(customerId, ct)).Where(x => x.Charge.Id != charge.Id));
            if (rest > 0) AddBalance(customerId.Value, CreditBalanceMovementType.VoidedChargePaid, rest, saleId);
        }
        var used = await db.BalanceMovements.Where(m => m.ReferenceId == saleId && m.Type == CreditBalanceMovementType.UsedInSale).ToListAsync(ct);
        foreach (var m in used) AddBalance(m.CustomerId, CreditBalanceMovementType.UseVoided, -m.Amount, saleId);
        await db.PublishBalanceAsync(outbox, customerId.Value, ct);
    }

    public async Task CreditReturnAsync(Guid customerId, Guid saleId, Guid returnId, decimal amount, CancellationToken ct)
    {
        await db.LockAsync(customerId, ct);
        // That sale's own charge first, then the oldest other debts; only the rest becomes credit balance.
        var open = (await db.OpenChargesAsync(customerId, ct)).OrderBy(x => x.Charge.SaleId == saleId ? 0 : 1).ToList();
        var rest = amount - ApplyPayment(customerId, CustomerPaymentSource.Return, returnId, amount, open);
        if (rest > 0) AddBalance(customerId, CreditBalanceMovementType.Return, rest, returnId);
        await db.PublishBalanceAsync(outbox, customerId, ct);
    }

    public async Task<Result> AddDepositAsync(Guid customerId, Guid orderId, decimal amount, CancellationToken ct)
    {
        if (await db.LockAsync(customerId, ct) == 0) return CustomersErrors.CustomerNotFound;
        AddBalance(customerId, CreditBalanceMovementType.Deposit, amount, orderId);
        await db.PublishBalanceAsync(outbox, customerId, ct); // R8 #13: the cloud sees the deposit in the balance
        return Result.Success();
    }

    public async Task<Result> RefundDepositAsync(Guid customerId, Guid orderId, decimal amount, CancellationToken ct)
    {
        if (await db.LockAsync(customerId, ct) == 0) return CustomersErrors.CustomerNotFound;
        if (await db.CreditBalanceAsync(customerId, ct) < amount) return CustomersErrors.InsufficientCreditBalance;
        AddBalance(customerId, CreditBalanceMovementType.DepositRefund, -amount, orderId);
        await db.PublishBalanceAsync(outbox, customerId, ct);
        return Result.Success();
    }

    /// <summary>Applies an amount to open charges in the given order with one payment record. Returns what was applied.</summary>
    private decimal ApplyPayment(Guid customerId, CustomerPaymentSource source, Guid referenceId, decimal amount, IEnumerable<(OnAccountCharge Charge, decimal Balance)> open)
    {
        var allocated = Allocation.InOrder(amount, open);
        if (allocated.Count == 0) return 0;
        var payment = new CustomerPayment
        {
            Id = Guid.CreateVersion7(), CustomerId = customerId, DateUtc = clock.GetUtcNow().UtcDateTime, Amount = allocated.Sum(x => x.Amount), Source = source,
            ReferenceId = referenceId, UserId = user.UserId == Guid.Empty ? null : user.UserId,
        };
        payment.Applications.AddRange(allocated.Select(x => new CustomerPaymentApplication { CustomerPaymentId = payment.Id, ChargeId = x.Item.Id, Amount = x.Amount }));
        db.Payments.Add(payment);
        return payment.Amount;
    }

    private void AddBalance(Guid customerId, CreditBalanceMovementType type, decimal amount, Guid referenceId) =>
        db.BalanceMovements.Add(new CreditBalanceMovement
        {
            Id = Guid.CreateVersion7(), CustomerId = customerId, Type = type, Amount = amount, ReferenceId = referenceId, DateUtc = clock.GetUtcNow().UtcDateTime,
        });
}

/// <summary>Every customer's name and account for the cloud when the store connects (TK-174), so debts from before the connection show too.</summary>
internal sealed class CustomerSnapshot(CustomersDbContext db, IOutbox outbox) : ICloudSnapshot
{
    public async Task EnqueueAsync(CancellationToken ct)
    {
        var debts = (await db.OpenChargesAsync(null, ct)).GroupBy(x => x.Charge.CustomerId).ToDictionary(g => g.Key, g => g.Sum(x => x.Balance));
        var balances = await db.BalanceMovements.GroupBy(m => m.CustomerId).Select(g => new { g.Key, Amount = g.Sum(m => m.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, ct);
        foreach (var c in await db.Customers.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            outbox.Add(new CustomerBalanceChangedV1(c.Id, c.Name, debts.GetValueOrDefault(c.Id), balances.GetValueOrDefault(c.Id)));
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class CustomersDesignTimeFactory : Abastalia.BuildingBlocks.Persistence.DesignTimeFactory<CustomersDbContext>;
