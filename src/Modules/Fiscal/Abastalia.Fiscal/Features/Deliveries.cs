using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal.Features;

/// <summary>TK-153/TK-240: the 31 handed to electronic buyers, newest first, with the attempts and the last answer or error.</summary>
internal static class ListDeliveries
{
    public sealed record Item(Guid DocumentId, string ENcf, string BuyerRnc, DeliveryStatus Status, int Attempts, DateTime CreatedUtc, DateTime? NextAttemptUtc,
        DateTime? DeliveredUtc, string? Message);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/deliveries", async (FiscalDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Set<EcfDelivery>().AsNoTracking().OrderByDescending(d => d.CreatedUtc).Take(100)
                        .Select(d => new Item(d.DocumentId, d.ENcf, d.BuyerRnc, d.Status, d.Attempts, d.CreatedUtc, d.NextAttemptUtc, d.DeliveredUtc, d.Message))
                        .ToListAsync(ct)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-240: "Reintentar" a delivery that failed, was not received or found no electronic buyer: back to the queue now, with another 72 hours,
/// and the buyer looked up again in the DGII directory (it may have become an electronic issuer since).</summary>
internal static class RetryDelivery
{
    public sealed record Command(Guid DocumentId) : ICommand<ListDeliveries.Item>;

    public static readonly Error NotFound = Error.NotFound("Fiscal.DeliveryNotFound", "Esa entrega no existe.");
    public static readonly Error AlreadyDelivered = Error.Conflict("Fiscal.AlreadyDelivered", "El comprador ya recibió ese comprobante.");

    internal sealed class Handler(FiscalDbContext db, ReceiverDirectoryCache directory, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, ListDeliveries.Item>
    {
        public async Task<Result<ListDeliveries.Item>> Handle(Command c, CancellationToken ct)
        {
            var d = await db.Set<EcfDelivery>().SingleOrDefaultAsync(x => x.DocumentId == c.DocumentId, ct);
            if (d is null) return NotFound;
            if (d.Status == DeliveryStatus.Delivered) return AlreadyDelivered;
            var before = d.Status;
            (d.Status, d.Attempts, d.NextAttemptUtc, d.Message, d.CreatedUtc) = (DeliveryStatus.Pending, 0, null, null, clock.GetUtcNow().UtcDateTime);
            directory.Entries.TryRemove(d.BuyerRnc, out _);
            await audit.RecordAsync("fiscal.retry-delivery", "EcfDelivery", d.DocumentId, new { d.ENcf, d.BuyerRnc, Before = before.ToString() }, ct);
            return new ListDeliveries.Item(d.DocumentId, d.ENcf, d.BuyerRnc, d.Status, d.Attempts, d.CreatedUtc, d.NextAttemptUtc, d.DeliveredUtc, d.Message);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/deliveries/{documentId:guid}/retry", async (Guid documentId, ICommandHandler<Command, ListDeliveries.Item> h, CancellationToken ct) =>
                    (await h.Handle(new Command(documentId), ct)).ToHttp())
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
