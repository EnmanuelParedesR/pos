using System.Net.Http.Json;
using System.Text.Json;

namespace Toki.IntegrationTests.Infra;

/// <summary>A cashier with an open shift on a new register, ready to sell. e-NCF ranges are loaded once per app.</summary>
public sealed record Cashier(UserSession Session, HttpClient Client, Guid ShiftId, Guid RegisterId);

public static class Store
{
    private static readonly HashSet<TokiApp> WithSequences = [];

    private static int _lastPlu = 1000;

    public static async Task LoadSequencesAsync(this TokiApp app)
    {
        lock (WithSequences) if (!WithSequences.Add(app)) return;
        var admin = app.Client(await app.AdminAsync());
        await app.SetUpBusinessAsync();
        foreach (var type in new[] { 31, 32, 34 })
            (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = type, From = 1, To = 100_000, ExpiresOn = "2030-12-31" })).EnsureSuccessStatusCode();
    }

    /// <summary>Issuing needs the shop's RNC; the rest keeps its defaults.</summary>
    public static async Task SetUpBusinessAsync(this TokiApp app)
    {
        var admin = app.Client(await app.AdminAsync());
        var business = await admin.GetFromJsonAsync<JsonElement>("/api/settings/business");
        (await admin.PutAsJsonAsync("/api/settings/business", new
        {
            Name = "Carnicería de prueba", Rnc = "131245678", DiscountCapPercent = business.GetProperty("discountCapPercent").GetDecimal(),
        })).EnsureSuccessStatusCode();
    }

    /// <summary>A PLU no other test uses (PLUs are unique per store).</summary>
    public static string NewPlu() => Interlocked.Increment(ref _lastPlu).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A price label as the scale prints it: "22" + 4-digit PLU + amount in centavos (6 digits) + EAN-13 check digit.</summary>
    public static string PriceLabel(string plu, decimal amount)
    {
        var twelve = $"22{int.Parse(plu, System.Globalization.CultureInfo.InvariantCulture):0000}{(int)(amount * 100):000000}";
        var sum = twelve.Select((d, i) => (d - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return twelve + (10 - sum % 10) % 10;
    }

    /// <param name="loadSequences">False for tests that load their own e-NCF ranges.</param>
    public static async Task<Cashier> CashierWithShiftAsync(this TokiApp app, decimal openingFloat = 2000m, bool loadSequences = true)
    {
        if (loadSequences) await app.LoadSequencesAsync();
        var admin = app.Client(await app.AdminAsync());
        var register = (await (await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja " + Guid.NewGuid().ToString()[..4] })).Json()).GetProperty("id").GetGuid();
        var session = await app.UserAsync(Roles.Cashier);
        var client = app.Client(session);
        var shift = await client.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = openingFloat });
        shift.EnsureSuccessStatusCode();
        return new Cashier(session, client, (await shift.Json()).GetProperty("shiftId").GetGuid(), register);
    }

    public static async Task<HttpResponseMessage> SellAsync(this Cashier cashier, object sale) => await cashier.Client.PostAsJsonAsync("/api/sales", sale);

    /// <summary>A minimal sale body: lines (product, quantity), cash payment, expected total.</summary>
    public static object Sale(Guid shiftId, decimal total, decimal cash, params object[] lines) => new
    {
        SaleId = Guid.CreateVersion7(), ShiftId = shiftId, Lines = lines, Payments = new[] { new { Method = "Cash", Amount = cash } }, ExpectedTotal = total,
    };

    public static object Line(Guid productId, decimal quantity) => new { ProductId = productId, Quantity = quantity };

    public static async Task<Guid> ApproveAsync(this HttpClient c, string permission, Guid resource)
    {
        var r = await c.PostAsJsonAsync("/api/identity/approvals", new { Code = "admin", Pin = TokiApp.AdminPin, Permission = permission, ResourceId = resource });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("approvalId").GetGuid();
    }

    public static async Task<JsonElement> XReportAsync(this TokiApp app, Guid shiftId) =>
        await app.Client(await app.AdminAsync()).GetFromJsonAsync<JsonElement>($"/api/cash/shifts/{shiftId}/z");
}
