using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Persistence;
using Toki.CashRegister.Contracts;
using Toki.Customers.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Host.Cloud.Persistence;
using Toki.Identity.Contracts;
using Toki.Inventory.Contracts;
using Toki.Purchasing.Contracts;
using Toki.Sales.Contracts;

namespace Toki.Host.Cloud.Ingest;

internal delegate Task Projector(CloudDbContext db, InboxEvent e, CancellationToken ct);

/// <summary>One projector per known event type (the outbox stores the CLR full name as the type). Each one replaces the rows it owns by id,
/// so applying an event twice gives the same rows: nothing is ever added on top of what is there.
/// Tenant and store always come from the inbox row (the node's identity), never from the payload.
/// Voids and returns live in their own tables, so the order in which events arrive does not matter.
/// Names (users, registers) keep the newest by event time, so a snapshot or an old event arriving late never undoes a rename.</summary>
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
        [Name<ReturnRecordedV1>()] = (db, e, ct) =>
        {
            var r = Read<ReturnRecordedV1>(e);
            return Return(db, e, new ReturnRecordedV2(r.ReturnId, r.SaleId, r.CreditNoteENcf, r.Method, r.Total, r.Itbis, []), ct);
        },
        [Name<ReturnRecordedV2>()] = (db, e, ct) => Return(db, e, Read<ReturnRecordedV2>(e), ct),
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
        [Name<UserProfileChangedV1>()] = async (db, e, ct) =>
        {
            var u = Read<UserProfileChangedV1>(e);
            var row = await db.StoreUsers.FirstOrDefaultAsync(x => x.TenantId == e.TenantId && x.Id == u.UserId, ct);
            if (row is null) db.StoreUsers.Add(row = new ReplicaStoreUser { TenantId = e.TenantId, Id = u.UserId });
            else if (row.ChangedUtc > e.OccurredUtc) return;
            (row.StoreId, row.Name, row.Active, row.ChangedUtc) = (e.StoreId, u.Name, u.Active, e.OccurredUtc);
        },
        [Name<RegisterChangedV1>()] = async (db, e, ct) =>
        {
            var r = Read<RegisterChangedV1>(e);
            var row = await db.Registers.FirstOrDefaultAsync(x => x.TenantId == e.TenantId && x.Id == r.RegisterId, ct);
            if (row is null) db.Registers.Add(row = new ReplicaRegister { TenantId = e.TenantId, Id = r.RegisterId });
            else if (row.ChangedUtc > e.OccurredUtc) return;
            (row.StoreId, row.Name, row.ChangedUtc) = (e.StoreId, r.Name, e.OccurredUtc);
        },
        [Name<PurchasePostedV1>()] = async (db, e, ct) =>
        {
            var p = Read<PurchasePostedV1>(e);
            await db.Purchases.Where(x => x.TenantId == e.TenantId && x.Id == p.PurchaseId).ExecuteDeleteAsync(ct);
            db.Purchases.Add(new ReplicaPurchase
            {
                TenantId = e.TenantId, Id = p.PurchaseId, StoreId = e.StoreId, SupplierId = p.SupplierId, Ncf = p.Ncf, Subtotal = p.Subtotal, Itbis = p.Itbis,
                Total = p.Total, OnCredit = p.OnCredit, PostedUtc = e.OccurredUtc,
            });
        },
        [Name<PurchaseVoidedV1>()] = async (db, e, ct) =>
        {
            var v = Read<PurchaseVoidedV1>(e);
            await db.PurchaseVoids.Where(x => x.TenantId == e.TenantId && x.PurchaseId == v.PurchaseId).ExecuteDeleteAsync(ct);
            db.PurchaseVoids.Add(new ReplicaPurchaseVoid { TenantId = e.TenantId, PurchaseId = v.PurchaseId, StoreId = e.StoreId, Reason = v.Reason, VoidedUtc = e.OccurredUtc });
        },
        [Name<OnAccountChargeRecordedV1>()] = async (db, e, ct) =>
        {
            var c = Read<OnAccountChargeRecordedV1>(e);
            await db.CustomerCharges.Where(x => x.TenantId == e.TenantId && x.SaleId == c.SaleId).ExecuteDeleteAsync(ct);
            db.CustomerCharges.Add(new ReplicaCustomerCharge
            {
                TenantId = e.TenantId, SaleId = c.SaleId, StoreId = e.StoreId, CustomerId = c.CustomerId, Amount = c.Amount, ChargedUtc = e.OccurredUtc,
            });
        },
        [Name<CustomerPaymentRecordedV1>()] = async (db, e, ct) =>
        {
            var p = Read<CustomerPaymentRecordedV1>(e);
            await db.CustomerPayments.Where(x => x.TenantId == e.TenantId && x.Id == p.CustomerPaymentId).ExecuteDeleteAsync(ct);
            db.CustomerPayments.Add(new ReplicaCustomerPayment
            {
                TenantId = e.TenantId, Id = p.CustomerPaymentId, StoreId = e.StoreId, CustomerId = p.CustomerId, Amount = p.Amount, PaidUtc = e.OccurredUtc,
            });
        },
        [Name<ShiftOpenedV1>()] = async (db, e, ct) =>
        {
            var s = Read<ShiftOpenedV1>(e);
            await db.OpenedShifts.Where(x => x.TenantId == e.TenantId && x.ShiftId == s.ShiftId).ExecuteDeleteAsync(ct);
            db.OpenedShifts.Add(new ReplicaOpenedShift
            {
                TenantId = e.TenantId, ShiftId = s.ShiftId, StoreId = e.StoreId, RegisterId = s.RegisterId, CashierId = s.CashierId, OpeningFloat = s.OpeningFloat,
                OpenedUtc = e.OccurredUtc,
            });
        },
        [Name<CashMovementRecordedV1>()] = async (db, e, ct) =>
        {
            var m = Read<CashMovementRecordedV1>(e);
            await db.CashMovements.Where(x => x.TenantId == e.TenantId && x.EventId == e.EventId).ExecuteDeleteAsync(ct);
            db.CashMovements.Add(new ReplicaCashMovement
            {
                TenantId = e.TenantId, EventId = e.EventId, StoreId = e.StoreId, ShiftId = m.ShiftId, Type = m.Type, Method = m.Method.ToString(), Amount = m.Amount,
                ReferenceId = m.ReferenceId, OccurredUtc = e.OccurredUtc,
            });
        },
    };

    private static async Task Return(CloudDbContext db, InboxEvent e, ReturnRecordedV2 r, CancellationToken ct)
    {
        await db.ReturnLines.Where(x => x.TenantId == e.TenantId && x.ReturnId == r.ReturnId).ExecuteDeleteAsync(ct);
        await db.Returns.Where(x => x.TenantId == e.TenantId && x.Id == r.ReturnId).ExecuteDeleteAsync(ct);
        db.Returns.Add(new ReplicaReturn
        {
            TenantId = e.TenantId, Id = r.ReturnId, StoreId = e.StoreId, SaleId = r.SaleId, CreditNoteENcf = r.CreditNoteENcf, Method = r.Method,
            Total = r.Total, Itbis = r.Itbis, ReturnedUtc = e.OccurredUtc,
        });
        db.ReturnLines.AddRange(r.Lines.Select((l, i) => new ReplicaReturnLine
        {
            TenantId = e.TenantId, ReturnId = r.ReturnId, LineNumber = i + 1, StoreId = e.StoreId, ProductId = l.ProductId, PresentationId = l.PresentationId,
            Description = l.Description, Quantity = l.Quantity, Total = l.Total, Itbis = l.Itbis, UnitCost = l.Cost,
        }));
    }

    private static string Name<T>() => typeof(T).FullName!;

    /// <summary>Same options the node's outbox serialized with (web casing, enums as text).</summary>
    private static T Read<T>(InboxEvent e) => JsonSerializer.Deserialize<T>(e.Payload, Json.Options) ?? throw new JsonException($"Empty payload for {e.Type}.");
}
