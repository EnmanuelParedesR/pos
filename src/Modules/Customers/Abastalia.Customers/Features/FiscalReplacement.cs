using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.Fiscal.Contracts;

namespace Abastalia.Customers.Features;

/// <summary>TK-206: the on-account charge of a sale whose e-CF the DGII rejected takes the replacement e-NCF (TK-147), so the customer's
/// statement shows the valid document, as the sale and its receipt already do.</summary>
internal sealed class FollowFiscalReplacement(CustomersDbContext db) : IEventHandler<FiscalDocumentReplacedV1>
{
    public Task HandleAsync(FiscalDocumentReplacedV1 e, CancellationToken ct) =>
        db.Charges.Where(c => c.SaleId == e.ReferenceId && c.Document == e.RejectedENcf).ExecuteUpdateAsync(c => c.SetProperty(x => x.Document, e.ENcf), ct);
}
