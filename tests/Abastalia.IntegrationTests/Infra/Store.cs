using System.Net.Http.Json;
using System.Text.Json;

namespace Abastalia.IntegrationTests.Infra;

/// <summary>A cashier with an open shift on a new register, ready to sell. e-NCF ranges are loaded once per app.</summary>
public sealed record Cashier(UserSession Session, HttpClient Client, Guid ShiftId, Guid RegisterId);

public static class Store
{
    private static readonly HashSet<AbastaliaApp> WithSequences = [];

    private static int _lastPlu = 1000;

    public static async Task LoadSequencesAsync(this AbastaliaApp app)
    {
        lock (WithSequences) if (!WithSequences.Add(app)) return;
        var admin = app.Client(await app.AdminAsync());
        await app.SetUpBusinessAsync();
        foreach (var type in new[] { 31, 32, 34 })
            (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = type, From = 1, To = 100_000, ExpiresOn = "2030-12-31" })).EnsureSuccessStatusCode();
    }

    /// <summary>Issuing needs the shop's RNC; the rest keeps its defaults.</summary>
    public static async Task SetUpBusinessAsync(this AbastaliaApp app)
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
    public static async Task<Cashier> CashierWithShiftAsync(this AbastaliaApp app, decimal openingFloat = 2000m, bool loadSequences = true)
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

    /// <param name="summary">R11 A1: what the server said is approved (<c>errors.summary</c> of Approval.Required); empty for actions bound to
    /// their resource alone (a void).</param>
    public static async Task<Guid> ApproveAsync(this HttpClient c, string permission, Guid resource, string summary = "")
    {
        var r = await c.PostAsJsonAsync("/api/identity/approvals",
            new { Code = "admin", Pin = AbastaliaApp.AdminPin, Permission = permission, ResourceId = resource, Summary = summary });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("approvalId").GetGuid();
    }

    /// <summary>Like the register (Approver.WithSupervisorAsync): sends it alone; when the server asks for a supervisor, the owner approves
    /// exactly what the server summarized and it goes again with the approval.</summary>
    public static async Task<HttpResponseMessage> SupervisedAsync(this HttpClient c, Guid resource, Func<Guid?, Task<HttpResponseMessage>> send)
    {
        var r = await send(null);
        if (await Required(r) is not { } required) return r;
        return await send(await c.ApproveAsync(required.Permission, resource, required.Summary));
    }

    /// <summary>A sale the way the register charges it: one approval per permission the server asks for, all sent together.</summary>
    public static async Task<HttpResponseMessage> SellSupervisedAsync(this Cashier cashier, Guid saleId, Func<Guid[], object> sale)
    {
        List<Guid> approvals = [];
        while (true)
        {
            var r = await cashier.SellAsync(sale([.. approvals]));
            if (approvals.Count > 4 || await Required(r) is not { } required) return r;
            approvals.Add(await cashier.Client.ApproveAsync(required.Permission, saleId, required.Summary));
        }
    }

    private static async Task<(string Permission, string Summary)?> Required(HttpResponseMessage r)
    {
        if (r.StatusCode != System.Net.HttpStatusCode.Forbidden) return null;
        var problem = await r.Content.ReadFromJsonAsync<JsonElement>();
        if (problem.GetProperty("code").GetString() != "Approval.Required") return null;
        var errors = problem.GetProperty("errors");
        return (errors.GetProperty("permission")[0].GetString()!, errors.GetProperty("summary")[0].GetString()!);
    }

    /// <summary>The store stops asking a supervisor for this action (TK-210), as a one-person shop would.</summary>
    public static async Task DoNotAskSupervisorAsync(this AbastaliaApp app, string permission)
    {
        var admin = app.Client(await app.AdminAsync());
        var rules = await admin.GetFromJsonAsync<JsonElement>("/api/settings/approvals");
        var required = rules.EnumerateArray().Where(r => r.GetProperty("required").GetBoolean()).Select(r => r.GetProperty("permission").GetString())
            .Where(p => p != permission).ToList();
        (await admin.PutAsJsonAsync("/api/settings/approvals", new { Required = required })).EnsureSuccessStatusCode();
    }

    public static async Task<JsonElement> XReportAsync(this AbastaliaApp app, Guid shiftId) =>
        await app.Client(await app.AdminAsync()).GetFromJsonAsync<JsonElement>($"/api/cash/shifts/{shiftId}/z");
}
