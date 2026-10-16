using Abastalia.Client.UI.Api;
using Abastalia.Escuadra;

namespace Abastalia.Client.UI.State;

/// <summary>Orders and quotes on the screens (TK-193, TK-194).</summary>
public static class OrderView
{
    public static string Status(OrderItem o) => (o.Kind, o.Status) switch
    {
        (_, OrderStatus.Pending) when o.Kind == OrderKind.Quote && o.ValidUntil < Format.Today => "Vencida",
        (OrderKind.Quote, OrderStatus.Pending) => "Abierta",
        (OrderKind.Quote, OrderStatus.Delivered) => "Convertida en venta",
        (_, OrderStatus.Pending) => "Pendiente",
        (_, OrderStatus.Ready) => "Listo",
        (_, OrderStatus.Delivered) => "Entregado",
        _ => "Cancelado",
    };

    public static string Pill(OrderItem o) => o.Status switch
    {
        OrderStatus.Ready or OrderStatus.Delivered => "esc-pill--success",
        OrderStatus.Cancelled => "esc-pill--neutral",
        _ => o.Kind == OrderKind.Quote && o.ValidUntil < Format.Today ? "esc-pill--warning" : "esc-pill--neutral",
    };

    /// <summary>"Hoy 10:30 AM", "Mañana", "18/10/2026".</summary>
    public static string Pickup(OrderItem o) => Pickup(o.PickupDate, o.PickupTime);

    public static string Pickup(DateOnly? date, TimeOnly? time)
    {
        if (date is not { } day) return "";
        var text = day == Format.Today ? "Hoy" : day == Format.Today.AddDays(1) ? "Mañana" : day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return time is { } t ? $"{text} {t.ToString("h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("es-DO"))}" : text;
    }

    public static string Summary(OrderItem o) =>
        string.Join(", ", o.Lines.Select(l => $"{Format.Quantity(l.Quantity, l.Unit)} {l.Name}{(l.Note is null ? "" : $" ({l.Note})")}"));

    /// <summary>Puts the order or quote in the cart at today's prices, with its customer and deposit. Returns a notice when prices changed
    /// since it was made (or a product can no longer be sold), so the cashier tells the customer before charging.</summary>
    public static async Task<(Problem? Problem, string? Notice)> LoadAsync(AbastaliaApi api, Cart cart, OrderItem o)
    {
        CustomerDetail? customer = null;
        if (o.CustomerId is { } id)
        {
            var c = await api.GetAsync<CustomerDetail>($"/api/customers/{id}");
            if (c.Value is null) return (c.Problem, null);
            customer = c.Value;
        }
        var ids = string.Join("&", o.Lines.Select(l => l.ProductId).Distinct().Select(i => $"ids={i}"));
        var prices = await api.GetAsync<List<ProductPrice>>($"/api/inventory/products/prices?{ids}");
        if (prices.Value is null) return (prices.Problem, null);
        var byId = prices.Value.ToDictionary(p => (p.ProductId, p.PresentationId));

        cart.Clear();
        cart.Customer = customer;
        var changes = new List<string>();
        foreach (var l in o.Lines)
        {
            if (!byId.TryGetValue((l.ProductId, l.PresentationId), out var p))
            {
                changes.Add($"{l.Name} ya no está a la venta");
                continue;
            }
            var line = cart.Add(new ProductByCode(l.ProductId, l.PresentationId, l.Name, l.Unit, "", p.Price, p.WholesalePrice, 1m, null, null, [], null), null, l.Quantity, l.Note);
            if (line.Price != l.Price) changes.Add($"{l.Name}: {Format.Amount(l.Price)} → {Format.Amount(line.Price)}");
        }
        (cart.OrderId, cart.Deposit) = (o.Id, o.Deposit);
        if (changes.Count == 0) return (null, null);
        var difference = cart.Total - o.Total;
        return (null, $"Cambiaron precios desde que se hizo: {string.Join(" · ", changes)}. Antes {Format.WithCurrency(o.Total)}, ahora {Format.WithCurrency(cart.Total)} "
            + $"({(difference >= 0 ? "+" : "−")}{Format.Amount(Math.Abs(difference))}).");
    }
}
