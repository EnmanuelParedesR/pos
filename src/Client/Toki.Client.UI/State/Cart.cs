using Toki.Client.UI.Api;

namespace Toki.Client.UI.State;

public sealed record CartLine(Guid ProductId, Guid? PresentationId, string Name, string Unit, decimal ListPrice, decimal Quantity,
    string? ScaleLabel = null, decimal? LabelAmount = null, Discount? Discount = null, decimal? OverriddenPrice = null)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public decimal Price => OverriddenPrice ?? ListPrice;
    /// <summary>A price label carries the line total; otherwise price × quantity to the centavo.</summary>
    public decimal Gross => LabelAmount ?? Math.Round(Price * Quantity, 2, MidpointRounding.AwayFromZero);
    public decimal DiscountAmount => Discount is null ? 0m
        : Discount.IsPercentage ? Math.Round(Gross * Discount.Value / 100m, 2, MidpointRounding.AwayFromZero) : Math.Min(Discount.Value, Gross);
    public decimal Total => Gross - DiscountAmount;
    public bool ByWeight => Unit == nameof(UnitOfMeasure.Pound);

    public SaleLine ToSaleLine() => new(ProductId, PresentationId, Quantity,
        OverriddenPrice, Discount, null, ScaleLabel);
}

/// <summary>The sale being rung up. The server recomputes everything; this total is what the cashier sees and what it confirms (ExpectedTotal).</summary>
public sealed class Cart
{
    private readonly List<CartLine> _lines = [];

    /// <summary>Also the idempotency key of the sale: a retry after a timeout never charges twice.</summary>
    public Guid SaleId { get; private set; } = Guid.CreateVersion7();
    public IReadOnlyList<CartLine> Lines => _lines;
    public Guid? Selected { get; set; }
    public CustomerDetail? Customer { get; set; }
    public EcfType DocumentType { get; set; } = EcfType.Consumer32;
    public string? ManualRnc { get; set; }
    public string? ManualName { get; set; }

    public decimal Total => _lines.Sum(l => l.Total);
    public decimal Discounts => _lines.Sum(l => l.DiscountAmount);
    public int LineCount => _lines.Count;
    public bool IsEmpty => _lines.Count == 0;
    public CartLine? SelectedLine => _lines.Find(l => l.Id == Selected);

    /// <summary>A label already in the cart (the design's "etiqueta repetida"): the server would refuse it too.</summary>
    public bool HasLabel(string code) => _lines.Exists(l => l.ScaleLabel == code);

    /// <summary>Adds a scanned or tapped product. The same per-unit product without a label adds up on its line.</summary>
    public CartLine Add(ProductByCode p, string? scaleLabel, decimal quantity = 1m)
    {
        CartLine line;
        if (scaleLabel is not null && (p.LabelQuantity is not null || p.LabelAmount is not null))
        {
            // The API ignores Quantity for labels but validates it > 0; a price label's weight is shown as its equivalent.
            var labelQuantity = p.LabelQuantity ?? Math.Max(0.001m, Math.Round(p.LabelAmount!.Value / p.Price, 3));
            line = new(p.ProductId, null, p.Name, p.Unit, p.Price, labelQuantity, scaleLabel, p.LabelAmount);
            _lines.Add(line);
        }
        else if (p.Unit == nameof(UnitOfMeasure.Unit) && _lines.FindLastIndex(l => l.ProductId == p.ProductId && l.PresentationId == p.PresentationId
                     && l.ScaleLabel is null && l.Discount is null && l.OverriddenPrice is null) is var i and >= 0)
        {
            line = _lines[i] with { Quantity = _lines[i].Quantity + quantity };
            _lines[i] = line;
        }
        else
        {
            line = new(p.ProductId, p.PresentationId, p.Name, p.Unit, p.Price, quantity);
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
        (Selected, Customer, DocumentType, ManualRnc, ManualName) = (null, null, EcfType.Consumer32, null, null);
        SaleId = Guid.CreateVersion7();
    }

    public RegisterSale ToRegisterSale(Guid shiftId, List<Payment> payments, decimal expectedTotal, List<Guid> approvals) =>
        new(SaleId, shiftId, [.. _lines.Select(l => l.ToSaleLine())], payments, expectedTotal, DocumentType, Customer?.Id,
            Customer is null ? ManualRnc : null, Customer is null ? ManualName : null, PriceLevel.Retail, null, approvals);

    /// <summary>What a held sale stores on the server, and how it comes back.</summary>
    public SavedCart Save() => new([.. _lines], Customer, DocumentType, ManualRnc, ManualName);

    public void Restore(SavedCart saved)
    {
        Clear();
        _lines.AddRange(saved.Lines);
        (Customer, DocumentType, ManualRnc, ManualName) = (saved.Customer, saved.DocumentType, saved.ManualRnc, saved.ManualName);
        Selected = _lines.LastOrDefault()?.Id;
    }
}

public sealed record SavedCart(List<CartLine> Lines, CustomerDetail? Customer, EcfType DocumentType, string? ManualRnc, string? ManualName);
