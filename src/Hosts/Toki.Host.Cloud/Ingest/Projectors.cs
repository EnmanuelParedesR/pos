using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Persistence;
using Toki.CashRegister.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Host.Cloud.Persistence;
using Toki.Inventory.Contracts;
using Toki.Sales.Contracts;

namespace Toki.Host.Cloud.Ingest;

internal delegate Task Projector(CloudDbContext db, InboxEvent e, CancellationToken ct);

/// <summary>One projector per known event type (the outbox stores the CLR full name as the type). Each one replaces the rows it owns by id,
/// so applying an event twice gives the same rows: nothing is ever added on top of what is there.
/// Tenant and store always come from the inbox row (the node's identity), never from the payload.
/// Voids and returns live in their own tables, so the order in which events arrive does not matter.</summary>
internal static class Projectors
{
    public static readonly IReadOnlyDictionary<string, Projector> ByType = new Dictionary<string, Projector>
    {
        [Name<SaleRecordedV1>()] = async (db, e, ct) =>
        {
            var s = Read<SaleRecordedV1>(e);
            await db.SaleLines.Where(l => l.TenantId == e.TenantId && l.SaleId == s.SaleId).ExecuteDeleteAsync(ct);
            await db.SalePayments.Where(p => p.TenantId == e.TenantId && p.SaleId == s.SaleId).ExecuteDeleteAsync(ct);
            await db.Sales.Where(x => x.TenantId == e.TenantId && x.Id == s.SaleId).ExecuteDeleteAsync(ct);
            db.Sales.Add(new ReplicaSale
            {
                TenantId = e.TenantId, Id = s.SaleId, StoreId = e.StoreId, Number = s.Number, ShiftId = s.ShiftId, CashierId = s.CashierId,
                CustomerId = s.CustomerId, ENcf = s.ENcf, Total = s.Total, Itbis = s.Itbis, Discount = s.Discount, SoldUtc = e.OccurredUtc,
            });
            db.SaleLines.AddRange(s.Lines.Select((l, i) => new ReplicaSaleLine
            {
                TenantId = e.TenantId, SaleId = s.SaleId, LineNumber = i + 1, StoreId = e.StoreId, ProductId = l.ProductId, PresentationId = l.PresentationId,
                Description = l.Description, Quantity = l.Quantity, Total = l.Total, Itbis = l.Itbis, UnitCost = l.Cost,
            }));
            db.SalePayments.AddRange(s.Payments.Select((p, i) => new ReplicaSalePayment
            {
                TenantId = e.TenantId, SaleId = s.SaleId, LineNumber = i + 1, StoreId = e.StoreId, Method = p.Method, Amount = p.Amount,
            }));
        },
        [Name<SaleVoidedV1>()] = async (db, e, ct) =>
        {
            var v = Read<SaleVoidedV1>(e);
            await db.SaleVoids.Where(x => x.TenantId == e.TenantId && x.SaleId == v.SaleId).ExecuteDeleteAsync(ct);
            db.SaleVoids.Add(new ReplicaSaleVoid
            {
                TenantId = e.TenantId, SaleId = v.SaleId, StoreId = e.StoreId, CreditNoteENcf = v.CreditNoteENcf, Reason = v.Reason,
                SupervisorId = v.SupervisorId, VoidedUtc = e.OccurredUtc,
            });
        },
        [Name<ReturnRecordedV1>()] = async (db, e, ct) =>
        {
            var r = Read<ReturnRecordedV1>(e);
            await db.Returns.Where(x => x.TenantId == e.TenantId && x.Id == r.ReturnId).ExecuteDeleteAsync(ct);
            db.Returns.Add(new ReplicaReturn
            {
                TenantId = e.TenantId, Id = r.ReturnId, StoreId = e.StoreId, SaleId = r.SaleId, CreditNoteENcf = r.CreditNoteENcf, Method = r.Method,
                Total = r.Total, Itbis = r.Itbis, ReturnedUtc = e.OccurredUtc,
            });
        },
        [Name<ShiftClosedV1>()] = async (db, e, ct) =>
        {
            var s = Read<ShiftClosedV1>(e);
            await db.ClosedShifts.Where(x => x.TenantId == e.TenantId && x.ShiftId == s.ShiftId).ExecuteDeleteAsync(ct);
            db.ClosedShifts.Add(new ReplicaClosedShift
            {
                TenantId = e.TenantId, ShiftId = s.ShiftId, StoreId = e.StoreId, RegisterId = s.RegisterId, CashierId = s.CashierId, ZNumber = s.ZNumber,
                ExpectedCash = s.ExpectedCash, CountedCash = s.CountedCash, Difference = s.Difference, ClosedUtc = e.OccurredUtc,
            });
        },
        [Name<StockMovementRecordedV1>()] = async (db, e, ct) =>
        {
            var m = Read<StockMovementRecordedV1>(e);
            await db.StockMovements.Where(x => x.TenantId == e.TenantId && x.Id == m.MovementId).ExecuteDeleteAsync(ct);
            db.StockMovements.Add(new ReplicaStockMovement
            {
                TenantId = e.TenantId, Id = m.MovementId, StoreId = e.StoreId, ProductId = m.ProductId, WarehouseId = m.WarehouseId, Type = m.Type.ToString(),
                Quantity = m.Quantity, UnitCost = m.UnitCost, ReferenceId = m.ReferenceId, OccurredUtc = e.OccurredUtc,
            });
        },
        [Name<PriceChangedV1>()] = async (db, e, ct) =>
        {
            var p = Read<PriceChangedV1>(e);
            await db.PriceChanges.Where(x => x.TenantId == e.TenantId && x.EventId == e.EventId).ExecuteDeleteAsync(ct);
            db.PriceChanges.Add(new ReplicaPriceChange
            {
                TenantId = e.TenantId, EventId = e.EventId, StoreId = e.StoreId, ProductId = p.ProductId, OldPrice = p.OldPrice, NewPrice = p.NewPrice,
                OldWholesalePrice = p.OldWholesalePrice, NewWholesalePrice = p.NewWholesalePrice, ChangedUtc = e.OccurredUtc,
            });
        },
        [Name<FiscalDocumentIssuedV1>()] = async (db, e, ct) =>
        {
            var d = Read<FiscalDocumentIssuedV1>(e);
            await db.FiscalDocuments.Where(x => x.TenantId == e.TenantId && x.Id == d.DocumentId).ExecuteDeleteAsync(ct);
            db.FiscalDocuments.Add(new ReplicaFiscalDocument
            {
                TenantId = e.TenantId, Id = d.DocumentId, StoreId = e.StoreId, ENcf = d.ENcf, Type = d.Type.ToString(), ReferenceId = d.ReferenceId,
                Total = d.Total, Itbis = d.Itbis, IssuedUtc = e.OccurredUtc,
            });
        },
    };

    private static string Name<T>() => typeof(T).FullName!;

    /// <summary>Same options the node's outbox serialized with (web casing, enums as text).</summary>
    private static T Read<T>(InboxEvent e) => JsonSerializer.Deserialize<T>(e.Payload, Json.Options) ?? throw new JsonException($"Empty payload for {e.Type}.");
}
