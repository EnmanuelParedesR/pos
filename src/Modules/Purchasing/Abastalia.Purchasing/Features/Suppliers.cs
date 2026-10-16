using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Purchasing.Domain;
using Abastalia.SharedKernel;

namespace Abastalia.Purchasing.Features;

/// <summary>Creates (Id null) or updates a supplier.</summary>
internal static class SaveSupplier
{
    public sealed record Command(Guid? Id, string Name, string? Rnc, string? Phone, string? Contact, int CreditDays, bool IsActive = true) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(150);
            RuleFor(c => c.Rnc).Must(r => Rnc.IsValid(Rnc.Normalize(r))).When(c => !string.IsNullOrWhiteSpace(c.Rnc)).WithMessage("El RNC debe tener 9 dígitos (u 11 si es cédula).");
            RuleFor(c => c.Phone).MaximumLength(30);
            RuleFor(c => c.Contact).MaximumLength(100);
            RuleFor(c => c.CreditDays).InclusiveBetween(0, 365);
        }
    }

    internal sealed class Handler(PurchasingDbContext db, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Lock first on an update, so the audit head stays the last lock (a payment to this supplier takes them in that order).
            if (c.Id is { } id && await db.LockSupplierAsync(id, ct) == 0) return PurchasingErrors.SupplierNotFound;
            var s = c.Id is { } existing ? await db.Suppliers.SingleAsync(x => x.Id == existing, ct) : null;
            if (s is null) db.Suppliers.Add(s = new Supplier { Id = Guid.CreateVersion7() });
            s.Name = c.Name.Trim();
            s.Rnc = Rnc.Normalize(c.Rnc);
            s.Phone = c.Phone?.Trim();
            s.Contact = c.Contact?.Trim();
            s.CreditDays = c.CreditDays;
            s.IsActive = c.IsActive;
            await audit.RecordAsync("purchasing.save-supplier", "Supplier", s.Id, c, ct);
            return new Response(s.Id);
        }
    }

    internal sealed record Body(string Name, string? Rnc, string? Phone, string? Contact, int CreditDays, bool IsActive = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/purchasing/suppliers", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Name, b.Rnc, b.Phone, b.Contact, b.CreditDays, b.IsActive), ct)).ToHttp(r => Results.Created($"/api/purchasing/suppliers/{r.Id}", r)))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
            app.MapPut("/api/purchasing/suppliers/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Name, b.Rnc, b.Phone, b.Contact, b.CreditDays, b.IsActive), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
            app.MapGet("/api/purchasing/suppliers", async (string? search, PurchasingDbContext db, CancellationToken ct) =>
                {
                    var q = db.Suppliers.AsNoTracking();
                    if (!string.IsNullOrWhiteSpace(search)) q = q.Where(s => s.Name.Contains(search.Trim()) || s.Rnc == Rnc.Normalize(search));
                    return Results.Ok(await q.OrderBy(s => s.Name).Take(200).ToListAsync(ct));
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
        }
    }
}
