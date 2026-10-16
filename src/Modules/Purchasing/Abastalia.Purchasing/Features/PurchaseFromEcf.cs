using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Purchasing.Features;

/// <summary>TK-240: "Registrar compra" on a received e-CF (TK-154): the purchase form comes filled with the supplier (found by RNC, or created with the
/// name on the e-CF), the e-NCF, the date, the totals and ITBIS, and the lines matched to products bought from that supplier before. Like the AI reading
/// (TK-224) it only fills the form: a person picks the missing products and saves.</summary>
internal static class PurchaseFromEcf
{
    public sealed record Command(Guid ReceivedId) : ICommand<ReadSupplierInvoice.Response>;

    public static readonly Error NotFound = Error.NotFound("Purchasing.ReceivedEcfNotFound", "Ese comprobante recibido no existe.");
    public static readonly Error TestEnvironment = Error.Conflict("Purchasing.ReceivedEcfIsTest",
        "Ese comprobante es de un ambiente de prueba de la DGII: no es una compra real.");

    internal sealed class Handler(PurchasingDbContext db, ISupplierEcfs ecfs, ITaxpayerRegistry registry, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, ReadSupplierInvoice.Response>
    {
        public async Task<Result<ReadSupplierInvoice.Response>> Handle(Command c, CancellationToken ct)
        {
            if (await ecfs.ReceivedAsync(c.ReceivedId, ct) is not { } e) return NotFound;
            if (e.Environment != "ecf") return TestEnvironment;
            // Created before the review, so the form has the supplier; the registry name wins over the e-CF's, as in the DGII cross (TK-223).
            await SupplierByRnc.FindOrCreateAsync(db, registry, audit, e.IssuerRnc, e.IssuerName, ct);
            await db.SaveChangesAsync(ct);
            return await ReadSupplierInvoice.ReviewEcfAsync(db, registry, e, clock, ct, "Tomada del e-CF que envió el suplidor: los montos son los del comprobante.");
        }
    }

    internal sealed record Body(Guid ReceivedId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/purchases/from-ecf", async (Body b, ICommandHandler<Command, ReadSupplierInvoice.Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(b.ReceivedId), ct)).ToHttp())
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}
