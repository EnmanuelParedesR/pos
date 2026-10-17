using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.CashRegister.Contracts;
using Abastalia.Customers.Contracts;
using Abastalia.Customers.Domain;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Customers.Features;

/// <summary>Creates (Id null) or updates a customer. Quick add from the register only needs a name.
/// Changing the credit limit or wholesale level, or reactivating a blocked customer, needs credit.manage.</summary>
internal static class SaveCustomer
{
    public sealed record Command(Guid? Id, string Name, string? TaxId = null, string? Phone = null, string? Address = null,
        bool FiscalCredit = false, PriceLevel PriceLevel = PriceLevel.Retail, decimal CreditLimit = 0, bool IsActive = true)
        : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(150);
            RuleFor(c => c.TaxId).Must(r => Rnc.IsValid(Rnc.Normalize(r))).When(c => !string.IsNullOrWhiteSpace(c.TaxId))
                .WithMessage("El RNC debe tener 9 dígitos o la cédula 11.");
            RuleFor(c => c.TaxId).NotEmpty().When(c => c.FiscalCredit).WithMessage("Para crédito fiscal hace falta el RNC o la cédula.");
            RuleFor(c => c.Phone).MaximumLength(30);
            RuleFor(c => c.Address).MaximumLength(200);
            RuleFor(c => c.PriceLevel).IsInEnum();
            RuleFor(c => c.CreditLimit).GreaterThanOrEqualTo(0).PrecisionScale(19, 2, true);
        }
    }

    internal sealed class Handler(CustomersDbContext db, IUserContext user, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Lock first on an update, so the audit head stays the last lock (a sale to this customer takes them in that order).
            if (c.Id is { } id && await db.LockAsync(id, ct) == 0) return CustomersErrors.CustomerNotFound;
            var customer = c.Id is { } existing ? await db.Customers.SingleAsync(x => x.Id == existing, ct) : null;
            var credit = (customer?.CreditLimit ?? 0) != c.CreditLimit || (customer?.PriceLevel ?? PriceLevel.Retail) != c.PriceLevel
                         || (customer is { IsActive: false } && c.IsActive);
            if (credit && !user.Has(Permissions.CreditManage)) return CustomersErrors.CreditPermissionRequired;
            var rnc = Rnc.Normalize(c.TaxId);
            if (rnc is not null && await db.Customers.AnyAsync(x => x.TaxId == rnc && x.Id != c.Id, ct)) return CustomersErrors.DuplicateTaxId;

            if (customer is null) db.Customers.Add(customer = new Customer { Id = Guid.CreateVersion7(), CreatedUtc = clock.GetUtcNow().UtcDateTime });
            var before = new { customer.CreditLimit, customer.PriceLevel, customer.IsActive };
            customer.Name = c.Name.Trim();
            customer.TaxId = rnc;
            customer.Phone = c.Phone?.Trim();
            customer.Address = c.Address?.Trim();
            customer.FiscalCredit = c.FiscalCredit;
            customer.PriceLevel = c.PriceLevel;
            customer.CreditLimit = c.CreditLimit;
            customer.IsActive = c.IsActive;
            await audit.RecordAsync("customers.save", "Customer", customer.Id, new { before, after = c }, ct);
            await db.PublishBalanceAsync(outbox, customer.Id, ct); // the cloud names debtors (TK-174)
            return new Response(customer.Id);
        }
    }

    internal sealed record Body(string Name, string? TaxId = null, string? Phone = null, string? Address = null, bool FiscalCredit = false,
        PriceLevel PriceLevel = PriceLevel.Retail, decimal CreditLimit = 0, bool IsActive = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/customers", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Name, b.TaxId, b.Phone, b.Address, b.FiscalCredit, b.PriceLevel, b.CreditLimit, b.IsActive), ct))
                    .ToHttp(r => Results.Created($"/api/customers/{r.Id}", r)))
                .RequireAnyPermission(Permissions.CustomersManage, Permissions.CustomersQuickAdd).WithTags("Customers"); // R11 A9: the register adds; editing needs manage
            app.MapPut("/api/customers/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Name, b.TaxId, b.Phone, b.Address, b.FiscalCredit, b.PriceLevel, b.CreditLimit, b.IsActive), ct))
                    .ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.CustomersManage).WithTags("Customers");
        }
    }
}

internal static class SearchCustomers
{
    /// <param name="Address">So the back office can edit a customer without losing it (an update replaces every field).</param>
    public sealed record Item(Guid Id, string Name, string? TaxId, string? Phone, PriceLevel PriceLevel, decimal CreditLimit, bool IsActive,
        string? Address, bool FiscalCredit);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            // Blocked customers are listed only for whoever may reactivate them.
            app.MapGet("/api/customers", async (string? search, bool? includeInactive, IUserContext user, CustomersDbContext db, CancellationToken ct) =>
                {
                    var q = db.Customers.AsNoTracking();
                    if (includeInactive != true || !user.Has(Permissions.CreditManage)) q = q.Where(c => c.IsActive);
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        var rnc = Rnc.Normalize(search);
                        q = q.Where(c => c.Name.Contains(search.Trim()) || c.TaxId == rnc || c.Phone == search.Trim());
                    }
                    return Results.Ok(await q.OrderBy(c => c.Name).Take(50)
                        .Select(c => new Item(c.Id, c.Name, c.TaxId, c.Phone, c.PriceLevel, c.CreditLimit, c.IsActive, c.Address, c.FiscalCredit)).ToListAsync(ct));
                })
                .RequireAnyPermission(Permissions.CustomersManage, Permissions.CustomersQuickAdd, Permissions.CreditCollect).WithTags("Customers");
            app.MapGet("/api/customers/{id:guid}", async (Guid id, ICustomerAccounts accounts, CancellationToken ct) =>
                    await accounts.GetAsync(id, ct) is { } c ? Results.Ok(c) : EndpointExtensions.Problem(CustomersErrors.CustomerNotFound))
                .RequireAnyPermission(Permissions.CustomersManage, Permissions.CustomersQuickAdd, Permissions.CreditCollect).WithTags("Customers");
        }
    }
}

/// <summary>A customer pays on their credit at the register: the money enters the shift and goes to the oldest charges first.
/// Cash comes in whole pesos; centavos above the debt (under RD$ 1) stay in the drawer as rounding, like a sale's change.</summary>
internal static class RecordCustomerPayment
{
    /// <param name="CustomerPaymentId">Generated by the register (GUID v7): a retry after a network drop returns the same payment instead of a second one.</param>
    public sealed record Command(Guid CustomerId, Guid CustomerPaymentId, Guid ShiftId, decimal Amount, PaymentMethod Method, string? Reference = null) : ICommand<Response>;
    public sealed record Response(Guid CustomerPaymentId, decimal Applied, decimal RemainingDebt);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.CustomerPaymentId).NotEmpty();
            RuleFor(c => c.Amount).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Amount).Must(m => m == Math.Truncate(m)).When(c => c.Method == PaymentMethod.Cash).WithMessage("El efectivo se recibe en pesos enteros.");
            RuleFor(c => c.Method).Must(m => m is PaymentMethod.Cash or PaymentMethod.Card or PaymentMethod.Transfer).WithMessage("Un abono se paga en efectivo, tarjeta o transferencia.");
            RuleFor(c => c.Reference).MaximumLength(60);
        }
    }

    internal sealed class Handler(CustomersDbContext db, ICashRegisterOperations register, ISettingsQueries settings, IUserContext user, IOutbox outbox,
        IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await SavedAsync(c, ct) is { } already) return already;
            // R11 A16 (TK-268): like a sale, card and transfer carry their reference unless the store turned it off.
            if (c.Method is PaymentMethod.Card or PaymentMethod.Transfer && string.IsNullOrWhiteSpace(c.Reference)
                && (await settings.GetBusinessProfileAsync(ct)).RequirePaymentReference)
                return CustomersErrors.ReferenceRequired;
            // Shift first, then the customer: the same lock order as a sale. A concurrent retry waits here and then finds the payment.
            if (await register.GetOpenShiftAsync(c.ShiftId, ct) is { IsFailure: true } shift) return shift.Error!;
            if (await db.LockAsync(c.CustomerId, ct) == 0) return CustomersErrors.CustomerNotFound;
            if (await SavedAsync(c, ct) is { } repeated) return repeated;

            var open = await db.OpenChargesAsync(c.CustomerId, ct);
            var debt = open.Sum(x => x.Balance);
            var applied = Math.Min(c.Amount, debt);
            var rounding = c.Amount - applied;
            if (rounding > 0 && (c.Method != PaymentMethod.Cash || rounding >= 1)) return CustomersErrors.PaymentExceedsDebt;

            var payment = new CustomerPayment
            {
                Id = c.CustomerPaymentId, CustomerId = c.CustomerId, DateUtc = clock.GetUtcNow().UtcDateTime, Amount = applied, Source = CustomerPaymentSource.Register,
                Method = c.Method, ShiftId = c.ShiftId, Reference = c.Reference?.Trim(), UserId = user.UserId,
            };
            payment.Applications.AddRange(Allocation.InOrder(applied, open)
                .Select(x => new CustomerPaymentApplication { CustomerPaymentId = payment.Id, ChargeId = x.Item.Id, Amount = x.Amount }));
            if (await register.RecordCustomerPaymentAsync(c.ShiftId, payment.Id, c.Method, applied, rounding, ct) is { IsFailure: true } inRegister) return inRegister.Error!;
            db.Payments.Add(payment);
            outbox.Add(new CustomerPaymentRecordedV1(c.CustomerId, payment.Id, applied));
            await audit.RecordAsync("customers.payment", "Customer", c.CustomerId, new { Payment = payment.Id, c.Amount, applied, c.Method, c.ShiftId }, ct);
            await db.PublishBalanceAsync(outbox, c.CustomerId, ct);
            return new Response(payment.Id, applied, debt - applied);
        }

        private async Task<Response?> SavedAsync(Command c, CancellationToken ct) =>
            await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == c.CustomerPaymentId, ct) is { } p
                ? new Response(p.Id, p.Amount, (await db.OpenChargesAsync(p.CustomerId, ct)).Sum(x => x.Balance))
                : null;
    }

    internal sealed record Body(Guid CustomerPaymentId, Guid ShiftId, decimal Amount, PaymentMethod Method, string? Reference = null);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/customers/{id:guid}/payments", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.CustomerPaymentId, b.ShiftId, b.Amount, b.Method, b.Reference), ct)).ToHttp())
                .RequirePermission(Permissions.CreditCollect).WithTags("Customers");
    }
}

/// <summary>Voids a payment typed wrong at the register, while its shift is open: the money leaves the drawer and the charges are open again.
/// Needs credit.manage or a supervisor.</summary>
internal static class VoidCustomerPayment
{
    public sealed record Command(Guid CustomerPaymentId, string Reason, Guid? SupervisorApprovalId) : ICommand<Ok>, IRequiresApproval
    {
        public string Permission => Permissions.CreditManage;
        public Guid ResourceId => CustomerPaymentId;
    }

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
    }

    internal sealed class Handler(CustomersDbContext db, ICashRegisterOperations register, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var customerId = await db.Payments.Where(p => p.Id == c.CustomerPaymentId && p.Source == CustomerPaymentSource.Register).Select(p => (Guid?)p.CustomerId).FirstOrDefaultAsync(ct);
            if (customerId is null) return CustomersErrors.PaymentNotFound;
            // Shift (fails when it is closed), then the customer, where the state is checked again: a second void rolls back here.
            if (await register.VoidMovementsAsync(c.CustomerPaymentId, ct) is { IsFailure: true } inRegister) return inRegister.Error!;
            await db.LockAsync(customerId.Value, ct);
            var payment = await db.Payments.Include(p => p.Applications).SingleAsync(p => p.Id == c.CustomerPaymentId, ct);
            if (payment.IsVoided) return CustomersErrors.PaymentAlreadyVoided;
            var charges = payment.Applications.Select(a => a.ChargeId).ToList();
            if (await db.Charges.AnyAsync(x => charges.Contains(x.Id) && x.IsVoided, ct)) return CustomersErrors.PaymentOnVoidedSale;

            payment.IsVoided = true;
            payment.VoidedUtc = clock.GetUtcNow().UtcDateTime;
            payment.VoidReason = c.Reason.Trim();
            await audit.RecordAsync("customers.void-payment", "Customer", payment.CustomerId, new { Payment = payment.Id, payment.Amount, c.Reason }, ct);
            await db.PublishBalanceAsync(outbox, payment.CustomerId, ct); // TK-174: the only event that tells the cloud the debt went back up
            return Ok.Value;
        }
    }

    internal sealed record Body(string Reason, Guid? SupervisorApprovalId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/customers/payments/{id:guid}/void", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Reason, b.SupervisorApprovalId), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.CreditCollect).WithTags("Customers");
    }
}

/// <summary>Customer statement with the running debt. Voided sales and payments show the original line and its reversal,
/// so the running debt always ends at the debt on the customer card.</summary>
internal static class CustomerStatement
{
    /// <param name="PaymentId">Set on a live payment taken at the register: the one the back office can void.</param>
    public sealed record StatementLine(DateTime DateUtc, string Type, string? Document, decimal Charge, decimal Payment, decimal Debt, Guid? PaymentId);
    public sealed record Response(Guid CustomerId, string Name, decimal Debt, decimal CreditBalance, decimal CreditLimit, IReadOnlyList<StatementLine> Lines);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/customers/{id:guid}/statement", async (Guid id, CustomersDbContext db, CancellationToken ct) =>
                {
                    var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (c is null) return EndpointExtensions.Problem(CustomersErrors.CustomerNotFound);
                    var entries = new List<(DateTime Date, string Type, string? Doc, decimal Charge, decimal Payment, Guid? PaymentId)>();
                    foreach (var x in await db.Charges.Where(x => x.CustomerId == id).AsNoTracking().ToListAsync(ct))
                    {
                        entries.Add((x.DateUtc, "Venta a crédito", x.Document, x.Amount, 0, null));
                        if (x.VoidedUtc is not { } voided) continue;
                        entries.Add((voided, "Venta anulada", x.Document, 0, x.Amount, null));
                        if (x.PaidAtVoid > 0) entries.Add((voided, "Lo pagado de la venta anulada", x.Document, x.PaidAtVoid, 0, null));
                    }
                    foreach (var p in await db.Payments.Where(x => x.CustomerId == id).AsNoTracking().ToListAsync(ct))
                    {
                        entries.Add((p.DateUtc, p.Source switch { CustomerPaymentSource.Register => "Abono", CustomerPaymentSource.Return => "Devolución", _ => "Pago de venta anulada" },
                            p.Reference, 0, p.Amount, p.Source == CustomerPaymentSource.Register && !p.IsVoided ? p.Id : null));
                        if (p.VoidedUtc is { } voided) entries.Add((voided, "Abono anulado", p.Reference, p.Amount, 0, null));
                    }
                    var debt = 0m;
                    var lines = entries.OrderBy(m => m.Date).Select(m => new StatementLine(m.Date, m.Type, m.Doc, m.Charge, m.Payment, debt += m.Charge - m.Payment, m.PaymentId)).ToList();
                    var open = (await db.OpenChargesAsync(id, ct)).Sum(x => x.Balance);
                    return Results.Ok(new Response(c.Id, c.Name, open, await db.CreditBalanceAsync(id, ct), c.CreditLimit, lines));
                })
                .RequirePermission(Permissions.CustomersManage).WithTags("Customers");
    }
}

/// <summary>Receivables by customer, aged by the business day of each unpaid charge (0–30, 31–60, 61–90, over 90 days); served and exported by Reports.</summary>
internal sealed class CustomerReports(CustomersDbContext db, TimeProvider clock) : ICustomerReports
{
    public async Task<IReadOnlyList<CustomerReceivable>> ReceivablesAsync(CancellationToken ct)
    {
        var today = BusinessDay.Of(clock.GetUtcNow().UtcDateTime);
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        return (await db.OpenChargesAsync(null, ct)).GroupBy(a => a.Charge.CustomerId).Select(g =>
        {
            decimal Range(int from, int to) => g.Where(a => today.DayNumber - BusinessDay.Of(a.Charge.DateUtc).DayNumber is var d && d >= from && d <= to).Sum(a => a.Balance);
            var customer = customers[g.Key];
            return new CustomerReceivable(g.Key, customer.Name, Range(int.MinValue, 30), Range(31, 60), Range(61, 90), Range(91, int.MaxValue), g.Sum(a => a.Balance),
                customer.CreditLimit);
        }).OrderByDescending(i => i.Total).ToList();
    }
}
