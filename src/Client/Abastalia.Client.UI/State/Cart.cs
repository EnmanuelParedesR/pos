using Abastalia.Client.UI.Api;

namespace Abastalia.Client.UI.State;

/// <param name="ListPrice">The retail price.</param>
/// <param name="Wholesale">The sale's customer buys at wholesale; the cart keeps it in step with its customer.</param>
public sealed record CartLine(Guid ProductId, Guid? PresentationId, string Name, string Unit, decimal ListPrice, decimal Quantity,
    string? ScaleLabel = null, decimal? LabelAmount = null, Discount? Discount = null, decimal? OverriddenPrice = null,
    decimal? WholesalePrice = null, bool Wholesale = false, string? Note = null)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>A wholesale customer pays the wholesale price where the product has one, as the server prices it.</summary>
    public bool UsesWholesale => Wholesale && WholesalePrice is not null;
    public decimal BasePrice => UsesWholesale ? WholesalePrice!.Value : ListPrice;
    public decimal Price => OverriddenPrice ?? BasePrice;
    /// <summary>A price label carries the line total; otherwise price × quantity to the centavo.</summary>
    public decimal Gross => LabelAmount ?? Math.Round(Price * Quantity, 2, MidpointRounding.AwayFromZero);
    /// <summary>Not capped: the server refuses a fixed discount bigger than the line, so the screen shows it as an error instead.</summary>
    public decimal DiscountAmount => Discount is null ? 0m
        : Discount.IsPercentage ? Math.Round(Gross * Discount.Value / 100m, 2, MidpointRounding.AwayFromZero) : Discount.Value;
    public bool DiscountTooBig => DiscountAmount > Gross;
    public decimal Total => Gross - DiscountAmount;
    public bool ByWeight => Unit == nameof(UnitOfMeasure.Pound);

    public SaleLine ToSaleLine() => new(ProductId, PresentationId, Quantity,
        OverriddenPrice, Discount, Note, ScaleLabel);
}

/// <summary>The sale being rung up. The server recomputes everything; this total is what the cashier sees and what it confirms (ExpectedTotal).</summary>
public sealed class Cart
{
    private readonly List<CartLine> _lines = [];

    /// <summary>Also the idempotency key of the sale: a retry after a timeout never charges twice.</summary>
    public Guid SaleId { get; private set; } = Guid.CreateVersion7();
    /// <summary>A checkout got no definitive answer (timeout, server error): the server may have saved it. Until someone asks
    /// the server (GET /api/sales/{SaleId}), the cart cannot change, be held or voided, or the saved sale would not match it.</summary>
    public bool CheckoutUncertain { get; set; }
    public IReadOnlyList<CartLine> Lines => _lines;
    public Guid? Selected { get; set; }
    /// <summary>Choosing a wholesale customer re-prices the lines at wholesale; any other customer (or none) puts them back at retail.</summary>
    public CustomerDetail? Customer
    {
        get;
        set
        {
            field = value;
            for (var i = 0; i < _lines.Count; i++) _lines[i] = _lines[i] with { Wholesale = IsWholesale };
        }
    }
    public bool IsWholesale => Customer?.PriceLevel == nameof(PriceLevel.Wholesale);
    public EcfType DocumentType { get; set; } = EcfType.Consumer32;
    public string? ManualRnc { get; set; }
    public string? ManualName { get; set; }
    /// <summary>The order or quote being charged (TK-193, TK-194): the sale marks it delivered.</summary>
    public Guid? OrderId { get; set; }
    /// <summary>The order's deposit, waiting in the customer's credit balance: the checkout uses it first.</summary>
    public decimal Deposit { get; set; }

    public decimal Total => _lines.Sum(l => l.Total);
    public decimal Discounts => _lines.Sum(l => l.DiscountAmount);
    public int LineCount => _lines.Count;
    public bool IsEmpty => _lines.Count == 0;
    public bool HasInvalidLine => _lines.Exists(l => l.DiscountTooBig);
    public CartLine? SelectedLine => _lines.Find(l => l.Id == Selected);

    /// <summary>A label already in the cart (the design's "etiqueta repetida"): the server would refuse it too.</summary>
    public bool HasLabel(string code) => _lines.Exists(l => l.ScaleLabel == code);

    /// <summary>Adds a scanned or tapped product. The same per-unit product without a label adds up on its line.</summary>
    public CartLine Add(ProductByCode p, string? scaleLabel, decimal quantity = 1m, string? note = null)
    {
        CartLine line;
        // A presentation (a 2 lb pack) is sold by the piece even when its product is sold by the pound.
        var unit = p.PresentationId is null ? p.Unit : nameof(UnitOfMeasure.Unit);
        if (scaleLabel is not null && (p.LabelQuantity is not null || p.LabelAmount is not null))
        {
            // The API ignores Quantity for labels but validates it > 0; a price label's weight is shown as its equivalent.
            var price = IsWholesale ? p.WholesalePrice ?? p.Price : p.Price;
            var labelQuantity = p.LabelQuantity ?? Math.Max(0.001m, Math.Round(p.LabelAmount!.Value / price, 3));
            line = new(p.ProductId, null, p.Name, p.Unit, p.Price, labelQuantity, scaleLabel, p.LabelAmount, WholesalePrice: p.WholesalePrice, Wholesale: IsWholesale);
            _lines.Add(line);
        }
        else if (unit == nameof(UnitOfMeasure.Unit) && _lines.FindLastIndex(l => l.ProductId == p.ProductId && l.PresentationId == p.PresentationId
                     && l.ScaleLabel is null && l.Discount is null && l.OverriddenPrice is null) is var i and >= 0)
        {
            line = _lines[i] with { Quantity = _lines[i].Quantity + quantity };
            _lines[i] = line;
        }
        else
        {
            line = new(p.ProductId, p.PresentationId, p.Name, unit, p.Price, quantity, WholesalePrice: p.WholesalePrice, Wholesale: IsWholesale, Note: note);
            _lines.Add(line);
        }
        Selected = line.Id;
        return line;
    }

    public void Update(CartLine line)
    {
        var i = _lines.FindIndex(l => l.Id == line.Id);
        if (i >= 0) _lines[i] = line;
    }

    public void Remove(Guid id)
    {
        var i = _lines.FindIndex(l => l.Id == id);
        if (i < 0) return;
        _lines.RemoveAt(i);
        Selected = _lines.Count == 0 ? null : _lines[Math.Min(i, _lines.Count - 1)].Id;
    }

    public void Clear()
    {
        _lines.Clear();
        (Selected, Customer, DocumentType, ManualRnc, ManualName, CheckoutUncertain) = (null, null, EcfType.Consumer32, null, null, false);
        (OrderId, Deposit) = (null, 0);
        SaleId = Guid.CreateVersion7();
    }

    public enum HandOverResult { Kept, SetAside, Restored }

    private readonly Dictionary<Guid, (SavedCart Cart, Guid SaleId, bool Uncertain)> _setAside = [];
    private (Guid Id, string Name)? _owner;

    /// <summary>The cashier whose sale was set aside at the last sign-in (for the register's message).</summary>
    public string? SetAsideFor { get; private set; }

    /// <summary>R13: a sale waits on this PC for its cashier (memory only), so an automatic restart to update must wait too.</summary>
    public bool HasSetAside => _setAside.Count > 0;
    /// <summary>What the last sign-in did with the cart; the register shows it once.</summary>
    public HandOverResult? LastHandOver { get; set; }

    /// <summary>A sign-in (TK-304): a sale left on the screen when a session expired is the cashier's who rang it up, never the next user's.
    /// It waits on this PC for its cashier, who finds it back when they sign in again (memory only: closing the app loses it, like the cart).
    /// An uncertain checkout waits too, with its SaleId: only its cashier (or whoever sees all sales) can ask the server whether it was saved;
    /// anyone else would get "not found" and could charge it twice.</summary>
    public HandOverResult HandOver(Guid userId, string name) => (LastHandOver = Hand(userId, name)).Value;

    private HandOverResult Hand(Guid userId, string name)
    {
        var previous = _owner;
        _owner = (userId, name);
        SetAsideFor = null;
        if (previous is { } p && p.Id != userId && !IsEmpty)
        {
            _setAside[p.Id] = (Save(), SaleId, CheckoutUncertain);
            Clear();
            SetAsideFor = p.Name;
        }
        else if (previous?.Id == userId) return HandOverResult.Kept;
        if (!IsEmpty || !_setAside.Remove(userId, out var mine)) return SetAsideFor is null ? HandOverResult.Kept : HandOverResult.SetAside;
        Restore(mine.Cart);
        (SaleId, CheckoutUncertain) = (mine.SaleId, mine.Uncertain);
        return HandOverResult.Restored;
    }

    /// <summary>The server already used this id for a different sale (Sales.DuplicateId): this cart needs its own.</summary>
    public void NewSaleId() => SaleId = Guid.CreateVersion7();

    /// <summary>After Sales.TotalChanged: the lines take the server's current prices, so the screen matches what it charges.
    /// A changed price, a price label's amount and a discount stay as the cashier set them.</summary>
    public void UpdatePrices(IEnumerable<ProductPrice> prices)
    {
        var byId = prices.ToDictionary(p => (p.ProductId, p.PresentationId));
        for (var i = 0; i < _lines.Count; i++)
            if (byId.TryGetValue((_lines[i].ProductId, _lines[i].PresentationId), out var p))
                _lines[i] = _lines[i] with { ListPrice = p.Price, WholesalePrice = p.WholesalePrice };
    }

    public RegisterSale ToRegisterSale(Guid shiftId, List<Payment> payments, decimal expectedTotal, List<Guid> approvals) =>
        new(SaleId, shiftId, [.. _lines.Select(l => l.ToSaleLine())], payments, expectedTotal, DocumentType, Customer?.Id,
            Customer is null ? ManualRnc : null, Customer is null ? ManualName : null, IsWholesale ? PriceLevel.Wholesale : PriceLevel.Retail, approvals, OrderId);

    /// <summary>What a held sale stores on the server, and how it comes back.</summary>
    public SavedCart Save() => new([.. _lines], Customer, DocumentType, ManualRnc, ManualName, OrderId, Deposit);

    public void Restore(SavedCart saved)
    {
        Clear();
        _lines.AddRange(saved.Lines);
        (Customer, DocumentType, ManualRnc, ManualName) = (saved.Customer, saved.DocumentType, saved.ManualRnc, saved.ManualName);
        (OrderId, Deposit) = (saved.OrderId, saved.Deposit);
        Selected = _lines.LastOrDefault()?.Id;
    }
}

public sealed record SavedCart(List<CartLine> Lines, CustomerDetail? Customer, EcfType DocumentType, string? ManualRnc, string? ManualName,
    Guid? OrderId = null, decimal Deposit = 0);
