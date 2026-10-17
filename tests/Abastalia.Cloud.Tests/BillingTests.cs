using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Host.Cloud.Billing;

namespace Abastalia.Cloud.Tests;

/// <summary>Paddle's side of the API, in memory: the catalog (two pages), portal sessions and subscription changes.</summary>
public sealed class FakePaddle : HttpMessageHandler
{
    public const string Basic = "pri_basic_month", BasicYear = "pri_basic_year", Registers = "pri_registers_month", Invoices = "pri_invoices_month",
        ExtraRegister = "pri_register_month", ExtraStore = "pri_store_month", ExtraCashier = "pri_cashier_month";

    public List<(HttpMethod Method, string Path, string? Authorization, JsonElement Body)> Requests { get; } = [];

    private static object Price(string id, string? interval, string amount, string status = "active") => new
    {
        id, status, description = id, unit_price = new { amount, currency_code = "USD" },
        billing_cycle = interval is null ? null : new { interval, frequency = 1 },
    };

    /// <summary>Abastalia's products as the owner set them in Paddle (custom_data.abastalia), and one product that is not Abastalia's.</summary>
    public List<object> Products { get; } =
    [
        new
        {
            id = "pro_basic", name = "Una caja", status = "active",
            custom_data = new { abastalia = new { plan = "Default", quotas = new { SupplierInvoices = 25 } } },
            prices = new[] { Price(Basic, "month", "1000"), Price(BasicYear, "year", "10000"), Price("pri_old", "month", "900", "archived") },
        },
        new
        {
            id = "pro_registers", name = "Varias cajas", status = "active",
            custom_data = new { abastalia = new { plan = "MultiRegister", registers = 4, quotas = new { Invites = 77 } } },
            prices = new[] { Price(Registers, "month", "2000") },
        },
        new
        {
            id = "pro_stores", name = "Varias sucursales", status = "active",
            custom_data = new { abastalia = new { plan = "MultiStore", rateLimits = new { panelPerMinute = 2 } } },
            prices = Array.Empty<object>(),
        },
        new
        {
            id = "pro_invoices", name = "Lectura de facturas", status = "active",
            custom_data = new { abastalia = new { addOn = "SupplierInvoiceReading", quotas = new { SupplierInvoices = 300 } } },
            prices = new[] { Price(Invoices, "month", "500") },
        },
        new
        {
            id = "pro_register", name = "Caja adicional", status = "active",
            custom_data = new { abastalia = new { addOn = "ExtraRegister", registers = 1 } },
            prices = new[] { Price(ExtraRegister, "month", "600") },
        },
        new
        {
            id = "pro_store", name = "Sucursal adicional", status = "active",
            custom_data = new { abastalia = new { addOn = "ExtraStore", stores = 1 } },
            prices = new[] { Price(ExtraStore, "month", "2500") },
        },
        new
        {
            id = "pro_cashier", name = "Cajero adicional", status = "active",
            custom_data = new { abastalia = new { addOn = "ExtraCashier", cashiers = 1 } },
            prices = new[] { Price(ExtraCashier, "month", "200"), Price("pri_cashier_year", "year", "2000") },
        },
        new { id = "pro_other", name = "Otra cosa", status = "active", custom_data = (object?)null, prices = new[] { Price("pri_other", "month", "1") } },
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? default : await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
        return request.RequestUri.AbsolutePath switch
        {
            "/products" when request.RequestUri.Query.Contains("after=", StringComparison.Ordinal) => Json(new { data = Products.Skip(2), meta = new { pagination = new { has_more = false } } }),
            "/products" => Json(new
            {
                data = Products.Take(2),
                meta = new { pagination = new { has_more = true, next = "https://sandbox-api.paddle.com/products?include=prices&after=pro_registers" } },
            }),
            var p when p.EndsWith("/portal-sessions", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { data = new { id = "cpls_1", urls = new { general = new { overview = "https://customer-portal.paddle.com/cpl_1?token=x" } } } }),
            },
            "/discounts" when request.Method == HttpMethod.Post => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { data = new { id = $"dsc_{Interlocked.Increment(ref _discounts)}" } }) },
            var p when p.StartsWith("/subscriptions/", StringComparison.Ordinal) => Subscription(request.Method, p["/subscriptions/".Length..], body),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private int _discounts;

    /// <summary>TK-255: each subscription's status in Paddle (default active) and the discount set on it.</summary>
    public Dictionary<string, string> Statuses { get; } = [];
    public Dictionary<string, string?> Discounts { get; } = [];

    private HttpResponseMessage Subscription(HttpMethod method, string path, JsonElement body)
    {
        var id = path.Split('/')[0];
        lock (Discounts)
        {
            if (method == HttpMethod.Patch && !path.EndsWith("/preview", StringComparison.Ordinal) && body.TryGetProperty("discount", out var d))
                Discounts[id] = d.ValueKind == JsonValueKind.Object ? d.GetProperty("id").GetString() : null;
            var discount = Discounts.GetValueOrDefault(id);
            return Json(new
            {
                data = new
                {
                    id, status = Statuses.GetValueOrDefault(id, "active"), discount = discount is null ? null : new { id = discount, starts_at = (string?)null },
                    items = new[] { new { quantity = 1, price = new { id = Registers } }, new { quantity = 1, price = new { id = Invoices } } },
                },
            });
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

/// <summary>Billing on, in Paddle's sandbox, with a license key in memory (TK-249). The catalog syncs when a test asks (<see cref="SyncCatalogAsync"/>).</summary>
public sealed class BillingApp : CloudApp
{
    public const string Secret = "pdl_ntfset_01test_secret";
    public const string Basic = FakePaddle.Basic, BasicYear = FakePaddle.BasicYear, Registers = FakePaddle.Registers, Invoices = FakePaddle.Invoices,
        ExtraRegister = FakePaddle.ExtraRegister, ExtraStore = FakePaddle.ExtraStore, ExtraCashier = FakePaddle.ExtraCashier;

    public ECDsa LicenseKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public FakePaddle Paddle { get; } = new();

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Billing:Paddle:Environment", "sandbox");
        builder.UseSetting("Billing:Paddle:ApiKey", "pdl_sdbx_apikey_test");
        builder.UseSetting("Billing:Paddle:WebhookSecret", Secret);
        builder.UseSetting("Billing:Paddle:ClientToken", "test_client_token");
        builder.UseSetting("Billing:Paddle:SyncInBackground", "false");
        builder.UseSetting("Billing:License:PrivateKey", LicenseKey.ExportPkcs8PrivateKeyPem());
        builder.UseSetting("Billing:Referrals:QualifyingPlans:0", "MultiRegister"); // TK-255; appsettings' other two (Essential, Default) blanked
        builder.UseSetting("Billing:Referrals:QualifyingPlans:1", "");
        builder.UseSetting("Billing:Referrals:QualifyingPlans:2", "");
        builder.UseSetting("Billing:Referrals:DiscountType", "percentage");
        builder.UseSetting("Billing:Referrals:Amount", "10");
        builder.UseSetting("Billing:Referrals:MonthlyLimitPerReferrer", "3");
        builder.UseSetting("Billing:Referrals:LookupsPerMinute", "5");
        builder.ConfigureTestServices(s => s.AddHttpClient<PaddleApi>().ConfigurePrimaryHttpMessageHandler(() => Paddle));
    }

    public async Task<int> SyncCatalogAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaddleCatalogSync>().SyncAsync(CancellationToken.None);
    }

    public Task<HttpResponseMessage> SendAsync(object payload, DateTimeOffset? signedAt = null, string secret = Secret)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, PaddleWebhook.Path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Paddle-Signature", PaddleSignature.Header(body, secret, signedAt ?? Clock.GetUtcNow()));
        return CreateClient().SendAsync(request);
    }
}

public sealed class BillingTests(BillingApp app) : IClassFixture<BillingApp>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.SyncCatalogAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly DateTime PeriodEnd = new(2026, 11, 15, 15, 0, 0, DateTimeKind.Utc);

    private static object Subscription(string eventId, string type, Guid tenant, string status, DateTime occurred, DateTime periodEnd, params string[] prices) => new
    {
        event_id = eventId, event_type = type, occurred_at = occurred.ToString("O"), notification_id = "ntf_" + eventId,
        data = new
        {
            id = "sub_" + tenant.ToString("N"), status, customer_id = "ctm_" + tenant.ToString("N"),
            items = prices.GroupBy(p => p).Select(g => new { quantity = g.Count(), price = new { id = g.Key } }), // a price twice = quantity 2
            current_billing_period = new { starts_at = periodEnd.AddMonths(-1).ToString("O"), ends_at = periodEnd.ToString("O") },
            next_billed_at = status == "active" ? periodEnd.ToString("O") : null,
            billing_cycle = new { interval = "month", frequency = 1 },
            custom_data = new { tenantId = tenant.ToString() },
        },
    };

    private DateTime Now => app.Clock.GetUtcNow().UtcDateTime;

    private static string Id() => "evt_" + Guid.NewGuid().ToString("N");

    private static async Task<JsonElement> BillingAsync(Owner owner) => await owner.Client.GetFromJsonAsync<JsonElement>("/api/billing");

    [Fact]
    public async Task Only_a_fresh_event_signed_with_the_secret_is_taken()
    {
        var owner = await app.OwnerAsync();
        var payload = Subscription(Id(), "subscription.created", owner.TenantId, "active", Now, PeriodEnd, BillingApp.Basic);

        (await app.SendAsync(payload, secret: "pdl_ntfset_otro")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.SendAsync(payload, Now.AddMinutes(-6))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);  // too old
        (await app.SendAsync(payload, Now.AddMinutes(6))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);   // from the future
        var tampered = new HttpRequestMessage(HttpMethod.Post, PaddleWebhook.Path) { Content = new StringContent("""{"event_id":"evt_x","event_type":"subscription.created","data":{}}""", Encoding.UTF8, "application/json") };
        tampered.Headers.Add("Paddle-Signature", PaddleSignature.Header("{}"u8.ToArray(), BillingApp.Secret, Now));
        (await app.CreateClient().SendAsync(tampered)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var unsigned = await app.CreateClient().PostAsJsonAsync(PaddleWebhook.Path, payload);
        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await BillingAsync(owner)).GetProperty("status").GetString().ShouldBe("None");

        (await app.SendAsync(payload, Now.AddMinutes(-4))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BillingAsync(owner)).GetProperty("status").GetString().ShouldBe("Active");
    }

    [Fact]
    public async Task Each_event_is_applied_once_even_when_replayed_or_retried()
    {
        var owner = await app.OwnerAsync();
        var id = Id();
        (await app.SendAsync(Subscription(id, "subscription.created", owner.TenantId, "active", Now, PeriodEnd, BillingApp.Registers))).StatusCode.ShouldBe(HttpStatusCode.OK);
        // The same event id again, even with other content (a replay inside the 5 minutes): answered, not applied.
        (await app.SendAsync(Subscription(id, "subscription.created", owner.TenantId, "canceled", Now, PeriodEnd))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => app.SendAsync(Subscription(id, "subscription.created", owner.TenantId, "canceled", Now, PeriodEnd))));

        var billing = await BillingAsync(owner);
        (billing.GetProperty("status").GetString(), billing.GetProperty("plan").GetString()).ShouldBe(("Active", "MultiRegister"));
        (await app.InDbAsync(db => db.BillingEvents.CountAsync(e => e.Id == id))).ShouldBe(1);
    }

    [Fact]
    public async Task The_prices_bought_set_the_plan_and_add_ons_and_an_older_event_changes_nothing()
    {
        var owner = await app.OwnerAsync();
        (await app.SendAsync(Subscription(Id(), "subscription.created", owner.TenantId, "active", Now, PeriodEnd, BillingApp.Registers, BillingApp.Invoices)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var usage = await owner.Client.GetFromJsonAsync<JsonElement>("/api/usage");
        usage.GetProperty("plan").GetString().ShouldBe("MultiRegister");
        usage.GetProperty("addOns").EnumerateArray().Select(a => a.GetString()).ShouldBe(["SupplierInvoiceReading"]);
        var billing = await BillingAsync(owner);
        billing.GetProperty("paidThroughUtc").GetDateTime().ShouldBe(PeriodEnd);
        billing.GetProperty("canManage").GetBoolean().ShouldBeTrue();

        // Paddle does not deliver in order: a "trialing" from before arrives late and is ignored.
        (await app.SendAsync(Subscription(Id(), "subscription.trialing", owner.TenantId, "trialing", Now.AddHours(-1), PeriodEnd, BillingApp.Basic))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BillingAsync(owner)).GetProperty("plan").GetString().ShouldBe("MultiRegister");

        // Canceled: back to Default (Una caja) and no add-ons.
        (await app.SendAsync(Subscription(Id(), "subscription.canceled", owner.TenantId, "canceled", Now.AddMinutes(1), PeriodEnd))).StatusCode.ShouldBe(HttpStatusCode.OK);
        billing = await BillingAsync(owner);
        (billing.GetProperty("status").GetString(), billing.GetProperty("plan").GetString(), billing.GetProperty("addOns").GetArrayLength()).ShouldBe(("Canceled", "Default", 0));
    }

    [Fact]
    public async Task The_store_gets_a_license_signed_by_the_cloud_key_that_runs_out_with_what_was_paid()
    {
        // Own app: it moves the clock weeks ahead, which would leave the other tests' fixed periods in the past.
        await using var own = new BillingApp();
        await own.SyncCatalogAsync();
        var owner = await own.OwnerAsync();
        var node = await own.StoreWithNodeAsync(owner, "Carnicería La Esquina");
        async Task<string?> LicenseAsync() => (await (await node.Client.PostAsync("/api/nodes/license", null)).Json()).GetProperty("license").GetString();
        (await LicenseAsync()).ShouldBeNull(); // nothing paid

        (await own.SendAsync(Subscription(Id(), "subscription.created", owner.TenantId, "active", own.Now, PeriodEnd, BillingApp.Registers, BillingApp.Invoices))).EnsureSuccessStatusCode();
        var jws = await LicenseAsync();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Jws.Verify(jws!, other).IsFailure.ShouldBeTrue();
        var license = Jws.Verify(jws!, other, own.LicenseKey).Value; // the store trusts either key
        (license.Store, license.Plan, license.Limits["Registers"], license.ExpiresUtc).ShouldBe(("Carnicería La Esquina", "Pro", 4, PeriodEnd.AddDays(3))); // 4 registers: Paddle's custom_data wins over appsettings (3)
        license.Features.ShouldContain("SupplierInvoiceReading");
        license.Features.ShouldContain("ButcherModule");
        // Same subscription, same issue date: the store does not reinstall it every pull.
        Jws.Verify((await LicenseAsync())!, own.LicenseKey).Value.IssuedUtc.ShouldBe(license.IssuedUtc);

        // The renewal fails: past due. The paid period does not move, the plan stays while Paddle retries.
        own.Clock.Advance(TimeSpan.FromDays(31));
        (await own.SendAsync(Subscription(Id(), "subscription.past_due", owner.TenantId, "past_due", own.Now, PeriodEnd.AddMonths(1), BillingApp.Registers, BillingApp.Invoices)))
            .EnsureSuccessStatusCode();
        owner = await own.SignInAsync(owner.Email);
        var billing = await BillingAsync(owner);
        (billing.GetProperty("status").GetString(), billing.GetProperty("plan").GetString()).ShouldBe(("PastDue", "MultiRegister"));
        Jws.Verify((await LicenseAsync())!, own.LicenseKey).Value.ExpiresUtc.ShouldBe(PeriodEnd.AddDays(3)); // then the store's grace, Restricted and Blocked

        // Past the margin there is no license to hand out; the store's own grace, Restricted and then Blocked follow (TK-279).
        own.Clock.Advance(TimeSpan.FromDays(3));
        (await LicenseAsync()).ShouldBeNull();

        // Paid at last: active again, a new period.
        (await own.SendAsync(new
        {
            event_id = Id(), event_type = "transaction.completed", occurred_at = own.Now.ToString("O"),
            data = new
            {
                id = "txn_1", status = "completed", customer_id = "ctm_x", subscription_id = "sub_" + owner.TenantId.ToString("N"),
                items = new[] { new { price = new { id = BillingApp.Registers }, quantity = 1 } },
                billing_period = new { starts_at = PeriodEnd.ToString("O"), ends_at = PeriodEnd.AddMonths(1).ToString("O") },
                custom_data = (object?)null,
            },
        })).EnsureSuccessStatusCode();
        owner = await own.SignInAsync(owner.Email);
        billing = await BillingAsync(owner);
        (billing.GetProperty("status").GetString(), billing.GetProperty("addOns").GetArrayLength()).ShouldBe(("Active", 0));
        Jws.Verify((await LicenseAsync())!, own.LicenseKey).Value.ExpiresUtc.ShouldBe(PeriodEnd.AddMonths(1).AddDays(3));
    }

    [Fact]
    public async Task Checkout_hands_Paddle_js_the_prices_and_the_business_and_the_portal_opens_for_its_customer()
    {
        var owner = await app.OwnerAsync();
        var billing = await BillingAsync(owner);
        (billing.GetProperty("enabled").GetBoolean(), billing.GetProperty("environment").GetString(), billing.GetProperty("canManage").GetBoolean()).ShouldBe((true, "sandbox", false));
        billing.GetProperty("plans").EnumerateArray().Select(p => p.GetProperty("code").GetString()).ShouldBe(["Default", "MultiRegister"]); // MultiStore has no price

        var checkout = await (await owner.Client.PostAsJsonAsync("/api/billing/checkout", new { plan = "Default", addOns = new[] { "SupplierInvoiceReading" }, interval = "Monthly" })).Json();
        checkout.GetProperty("clientToken").GetString().ShouldBe("test_client_token");
        checkout.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("priceId").GetString()).ShouldBe([BillingApp.Basic, BillingApp.Invoices]);
        checkout.GetProperty("customerEmail").GetString().ShouldBe(owner.Email);
        checkout.GetProperty("customData").GetProperty("tenantId").GetString().ShouldBe(owner.TenantId.ToString());
        (await owner.Client.PostAsJsonAsync("/api/billing/checkout", new { plan = "MultiStore", interval = "Monthly" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.Client.PostAsJsonAsync("/api/billing/checkout", new { plan = "MultiRegister", interval = "Annual" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await (await owner.Client.PostAsync("/api/billing/portal", null)).ErrorCode()).ShouldBe("Billing.NoCustomer");

        (await app.SendAsync(Subscription(Id(), "subscription.created", owner.TenantId, "active", Now, PeriodEnd, BillingApp.Basic))).EnsureSuccessStatusCode();
        var portal = await owner.Client.PostAsync("/api/billing/portal", null);
        (await portal.Json()).GetProperty("url").GetString().ShouldStartWith("https://customer-portal.paddle.com/");
        var call = app.Paddle.Requests.Last(r => r.Path.EndsWith("/portal-sessions", StringComparison.Ordinal));
        (call.Path, call.Authorization).ShouldBe(($"/customers/ctm_{owner.TenantId:N}/portal-sessions", "Bearer pdl_sdbx_apikey_test"));

        // Subscribed already: adding invoice reading changes the subscription's items instead of a second checkout.
        (await (await owner.Client.PostAsJsonAsync("/api/billing/checkout", new { addOns = new[] { "SupplierInvoiceReading" } })).Json()).GetProperty("changed").GetBoolean().ShouldBeTrue();
        var change = app.Paddle.Requests.Last(r => r.Method == HttpMethod.Patch);
        change.Path.ShouldBe($"/subscriptions/sub_{owner.TenantId:N}");
        change.Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("price_id").GetString()).ShouldBe([BillingApp.Basic, BillingApp.Invoices]);
    }

    [Fact]
    public async Task Extra_registers_and_stores_are_bought_by_quantity_and_reach_the_license()
    {
        var owner = await app.OwnerAsync();
        var first = await app.StoreWithNodeAsync(owner, "Colmado Centro");
        app.Clock.Advance(TimeSpan.FromSeconds(1)); // stores are licensed oldest first
        var second = await app.StoreWithNodeAsync(owner, "Colmado Norte");
        static async Task<LicenseData?> LicenseAsync(HttpClient node, ECDsa key) =>
            (await (await node.PostAsync("/api/nodes/license", null)).Json()).GetProperty("license").GetString() is { } jws ? Jws.Verify(jws, key).Value : null;

        // Two extra registers: one item with quantity 2 at checkout.
        var checkout = await (await owner.Client.PostAsJsonAsync("/api/billing/checkout",
            new { plan = "MultiRegister", addOns = new[] { "ExtraRegister", "ExtraRegister" }, interval = "Monthly" })).Json();
        checkout.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("priceId").GetString(), i.GetProperty("quantity").GetInt32()))
            .ShouldBe([(BillingApp.Registers, 1), (BillingApp.ExtraRegister, 2)]);

        (await app.SendAsync(Subscription(Id(), "subscription.created", owner.TenantId, "active", Now, PeriodEnd, BillingApp.Registers, BillingApp.ExtraRegister, BillingApp.ExtraRegister)))
            .EnsureSuccessStatusCode();
        (await BillingAsync(owner)).GetProperty("addOns").EnumerateArray().Select(a => a.GetString()).ShouldBe(["ExtraRegister", "ExtraRegister"]);
        (await LicenseAsync(first.Client, app.LicenseKey))!.Limits["Registers"].ShouldBe(6); // Paddle's 4 + 2
        (await LicenseAsync(second.Client, app.LicenseKey)).ShouldBeNull();                  // the plan licenses one store

        // A "Sucursal adicional" licenses the second store, with the plan's registers.
        (await app.SendAsync(Subscription(Id(), "subscription.updated", owner.TenantId, "active", Now.AddMinutes(1), PeriodEnd,
            BillingApp.Registers, BillingApp.ExtraRegister, BillingApp.ExtraRegister, BillingApp.ExtraStore))).EnsureSuccessStatusCode();
        (await LicenseAsync(second.Client, app.LicenseKey))!.Store.ShouldBe("Colmado Norte");

        // TK-277: cashiers: the plan's 8 (appsettings) + 2 per extra register + 4 per extra store + 1 per "Cajero adicional" (Paddle's custom_data).
        (await LicenseAsync(first.Client, app.LicenseKey))!.Limits["Cashiers"].ShouldBe(8 + 2 * 2 + 4);
        (await app.SendAsync(Subscription(Id(), "subscription.updated", owner.TenantId, "active", Now.AddMinutes(2), PeriodEnd, BillingApp.Registers,
            BillingApp.ExtraRegister, BillingApp.ExtraRegister, BillingApp.ExtraStore, BillingApp.ExtraCashier, BillingApp.ExtraCashier, BillingApp.ExtraCashier)))
            .EnsureSuccessStatusCode();
        (await BillingAsync(owner)).GetProperty("addOns").EnumerateArray().Count(a => a.GetString() == "ExtraCashier").ShouldBe(3);
        (await LicenseAsync(first.Client, app.LicenseKey))!.Limits["Cashiers"].ShouldBe(8 + 2 * 2 + 4 + 3);

        // Subscribed: changing the quantity sends it to Paddle.
        (await owner.Client.PostAsJsonAsync("/api/billing/checkout", new { addOns = new[] { "ExtraRegister", "ExtraRegister", "ExtraRegister" } })).EnsureSuccessStatusCode();
        app.Paddle.Requests.Last(r => r.Method == HttpMethod.Patch).Body.GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("price_id").GetString(), i.GetProperty("quantity").GetInt32())).ShouldBe([(BillingApp.Registers, 1), (BillingApp.ExtraRegister, 3)]);
    }

    [Fact]
    public async Task Only_the_owner_sees_and_pays_and_staff_override_is_audited()
    {
        var owner = await app.OwnerAsync();
        var email = $"contable{Guid.NewGuid():N}@example.com";
        var link = (await (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" })).Json()).GetProperty("link").GetString()!;
        var invite = await app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = link[(link.IndexOf('#') + 1)..], password = CloudApp.Password });
        invite.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var member = await app.SignInAsync(email);
        (await member.Client.GetAsync("/api/billing")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await member.Client.PostAsJsonAsync("/api/billing/checkout", new { plan = "Default" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await member.Client.PostAsync("/api/billing/portal", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/billing", new { manualUntilUtc = PeriodEnd, note = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var staff = await app.StaffAsync();
        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/billing", new { manualUntilUtc = PeriodEnd })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/billing", new { manualUntilUtc = PeriodEnd, note = "Feria: 1 mes gratis" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var view = await staff.Client.GetFromJsonAsync<JsonElement>($"/api/platform/tenants/{owner.TenantId}/billing");
        view.GetProperty("subscription").GetProperty("manualNote").GetString().ShouldBe("Feria: 1 mes gratis");
        (await BillingAsync(owner)).GetProperty("manualUntilUtc").GetDateTime().ShouldBe(PeriodEnd);
        (await app.InDbAsync(db => db.PlatformAudit.AnyAsync(a => a.TenantId == owner.TenantId && a.Method == "PUT" && a.Path.EndsWith("/billing")))).ShouldBeTrue();
        // The override pays for the store's license too.
        var node = await app.StoreWithNodeAsync(owner);
        (await (await node.Client.PostAsync("/api/nodes/license", null)).Json()).GetProperty("license").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task TK279_staff_block_a_business_with_a_reason_and_its_stores_get_a_blocked_license_until_it_is_unblocked()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        async Task<LicenseData?> LicenseAsync() =>
            (await (await node.Client.PostAsync("/api/nodes/license", null)).Json()).GetProperty("license").GetString() is { } jws ? Jws.Verify(jws, app.LicenseKey).Value : null;
        (await LicenseAsync()).ShouldBeNull(); // nothing paid
        var url = $"/api/platform/tenants/{owner.TenantId}/block";
        (await owner.Client.PutAsJsonAsync(url, new { blocked = true, reason = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var staff = await app.StaffAsync();
        (await (await staff.Client.PutAsJsonAsync(url, new { blocked = true })).ErrorCode()).ShouldBe("Billing.ReasonRequired");
        (await staff.Client.PutAsJsonAsync(url, new { blocked = true, reason = "Sin pago desde agosto" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var blocked = await LicenseAsync(); // even without paid time: the block has to reach the store
        (blocked!.Blocked, blocked.TenantId).ShouldBe((true, owner.TenantId));
        var view = (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/platform/tenants/{owner.TenantId}/billing")).GetProperty("subscription");
        (view.GetProperty("blockedUtc").ValueKind, view.GetProperty("blockNote").GetString()).ShouldBe((JsonValueKind.String, "Sin pago desde agosto"));
        (await app.InDbAsync(db => db.PlatformAudit.AnyAsync(a => a.TenantId == owner.TenantId && a.Method == "PUT" && a.Path.EndsWith("/block")))).ShouldBeTrue();

        // Paid outside Paddle and unblocked: the next license is newer (the store installs it) and no longer blocked.
        app.Clock.Advance(TimeSpan.FromSeconds(2));
        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/billing", new { manualUntilUtc = PeriodEnd, note = "Transferencia" })).EnsureSuccessStatusCode();
        (await LicenseAsync())!.Blocked.ShouldBeTrue(); // a payment alone does not lift a block from the console
        app.Clock.Advance(TimeSpan.FromSeconds(2));
        (await staff.Client.PutAsJsonAsync(url, new { blocked = false, reason = "Pagó por transferencia" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var unblocked = await LicenseAsync();
        (unblocked!.Blocked, unblocked.ExpiresUtc).ShouldBe((false, PeriodEnd.AddDays(3)));
        unblocked.IssuedUtc.ShouldBeGreaterThan(blocked.IssuedUtc);

        // TK-287: every upload says when the license last changed, so the store asks for it then instead of at its next heartbeat.
        var upload = await (await node.SendAsync(new { id = Guid.NewGuid(), type = "Abastalia.Test.Ping", aggregateId = Guid.NewGuid(), occurredUtc = DateTime.UtcNow, payload = new { } })).Json();
        upload.GetProperty("licenseUtc").GetDateTime().ShouldBe(unblocked.IssuedUtc, TimeSpan.FromSeconds(1));
    }
}

/// <summary>TK-249: the catalog, prices and limits come from Paddle (products' custom_data.abastalia), merged over appsettings.</summary>
public sealed class BillingCatalogTests(BillingApp app) : IClassFixture<BillingApp>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.SyncCatalogAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_catalog_syncs_every_page_from_Paddle_and_the_public_prices_show_it()
    {
        (await app.SyncCatalogAsync()).ShouldBe(7); // Abastalia's seven products; "Otra cosa" has no custom_data.abastalia
        app.Paddle.Requests.Where(r => r.Path == "/products").Select(r => r.Authorization).ShouldAllBe(a => a == "Bearer pdl_sdbx_apikey_test");
        (await app.InDbAsync(db => db.PaddleProducts.CountAsync())).ShouldBe(8); // both pages stored

        var prices = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/public/prices"); // no account
        prices.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        var plans = prices.GetProperty("plans").EnumerateArray().ToList();
        plans.Select(p => p.GetProperty("code").GetString()).ShouldBe(["Default", "MultiRegister"]); // MultiStore has no price yet
        (plans[0].GetProperty("name").GetString(), plans[0].GetProperty("monthly").GetProperty("amount").GetString(),
            plans[0].GetProperty("monthly").GetProperty("currency").GetString(), plans[0].GetProperty("annual").GetProperty("amount").GetString())
            .ShouldBe(("Una caja", "1000", "USD", "10000")); // the archived price is not shown
        plans[1].GetProperty("annual").ValueKind.ShouldBe(JsonValueKind.Null);
        prices.GetProperty("addOns").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "SupplierInvoiceReading").GetProperty("monthly").GetProperty("priceId").GetString().ShouldBe(BillingApp.Invoices);

        // A price changed in Paddle: the next sync shows it, nothing in code.
        app.Paddle.Products[1] = new
        {
            id = "pro_registers", name = "Varias cajas", status = "active",
            custom_data = new { abastalia = new { plan = "MultiRegister", registers = 4, quotas = new { Invites = 77 } } }, // as before: only the amount changes
            prices = new[] { new { id = BillingApp.Registers, status = "active", unit_price = new { amount = "2500", currency_code = "USD" }, billing_cycle = new { interval = "month", frequency = 1 } } },
        };
        await app.SyncCatalogAsync();
        (await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/public/prices")).GetProperty("plans")[1].GetProperty("monthly").GetProperty("amount").GetString().ShouldBe("2500");
    }

    [Fact]
    public async Task A_product_or_price_webhook_asks_for_a_sync()
    {
        var signal = app.Services.GetRequiredService<CatalogSignal>();
        var before = signal.Requests;
        (await app.SendAsync(new { event_id = "evt_" + Guid.NewGuid().ToString("N"), event_type = "price.updated", occurred_at = app.Now.ToString("O"), data = new { id = "pri_x" } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        signal.Requests.ShouldBe(before + 1);
    }

    [Fact]
    public async Task Quotas_come_from_Paddle_then_appsettings_then_Default()
    {
        var owner = await app.OwnerAsync();
        static int Limit(JsonElement usage, string meter) =>
            usage.GetProperty("meters").EnumerateArray().Single(m => m.GetProperty("meter").GetString() == meter).GetProperty("limit").GetInt32();
        var usage = await owner.Client.GetFromJsonAsync<JsonElement>("/api/usage");
        Limit(usage, "SupplierInvoices").ShouldBe(25); // Paddle's Default product over appsettings' 20
        Limit(usage, "Invites").ShouldBe(3);           // not in Paddle: appsettings

        var staff = await app.StaffAsync();
        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/plan", new { plan = "MultiRegister", addOns = new[] { "SupplierInvoiceReading" } }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        usage = await owner.Client.GetFromJsonAsync<JsonElement>("/api/usage");
        Limit(usage, "Invites").ShouldBe(77);              // Paddle's MultiRegister over appsettings' 5
        Limit(usage, "AssistantQuestions").ShouldBe(600);  // appsettings' MultiRegister
        Limit(usage, "SupplierInvoices").ShouldBe(325);    // the add-on adds its 300 to Default's (Paddle) 25
    }

    [Fact]
    public async Task Per_minute_limits_follow_the_plan_in_Paddle()
    {
        var owner = await app.OwnerAsync();
        var other = await app.OwnerAsync();
        var staff = await app.StaffAsync();
        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/plan", new { plan = "MultiStore", addOns = Array.Empty<string>() }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        for (var i = 0; i < 2; i++) (await owner.Client.GetAsync("/api/usage")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Client.GetAsync("/api/usage")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests); // MultiStore: panelPerMinute 2
        for (var i = 0; i < 5; i++) (await other.Client.GetAsync("/api/usage")).StatusCode.ShouldBe(HttpStatusCode.OK); // Default: the setting
    }
}

/// <summary>Without <c>Billing:Paddle</c> (the default) billing is off: nothing to buy, no webhook, no license from the cloud.</summary>
public sealed class BillingOffTests(CloudApp app) : IClassFixture<CloudApp>
{
    [Fact]
    public async Task Billing_is_off_until_Paddle_is_configured()
    {
        var owner = await app.OwnerAsync();
        var billing = await owner.Client.GetFromJsonAsync<JsonElement>("/api/billing");
        (billing.GetProperty("enabled").GetBoolean(), billing.GetProperty("plan").GetString(), billing.GetProperty("plans").GetArrayLength()).ShouldBe((false, "Default", 0));
        (await (await owner.Client.PostAsJsonAsync("/api/billing/checkout", new { plan = "Default" })).ErrorCode()).ShouldBe("Billing.Off");
        (await (await owner.Client.PostAsync("/api/billing/portal", null)).ErrorCode()).ShouldBe("Billing.Off");
        (await app.CreateClient().PostAsJsonAsync(PaddleWebhook.Path, new { event_id = "evt_1" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var prices = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/public/prices"); // /precios says "Precio por confirmar"
        (prices.GetProperty("enabled").GetBoolean(), prices.GetProperty("plans").GetArrayLength()).ShouldBe((false, 0));
        var node = await app.StoreWithNodeAsync(owner);
        (await (await node.Client.PostAsync("/api/nodes/license", null)).Json()).GetProperty("license").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
