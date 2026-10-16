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
using Toki.BuildingBlocks.Security;
using Toki.Host.Cloud.Billing;

namespace Toki.Cloud.Tests;

/// <summary>Paddle's side of the API, in memory: portal sessions and subscription changes.</summary>
public sealed class FakePaddle : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Authorization, JsonElement Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? default : await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
        return request.RequestUri.AbsolutePath switch
        {
            var p when p.EndsWith("/portal-sessions", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { data = new { id = "cpls_1", urls = new { general = new { overview = "https://customer-portal.paddle.com/cpl_1?token=x" } } } }),
            },
            var p when p.StartsWith("/subscriptions/", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new { } }) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }
}

/// <summary>Billing on, in Paddle's sandbox, with a license key in memory (TK-249).</summary>
public sealed class BillingApp : CloudApp
{
    public const string Secret = "pdl_ntfset_01test_secret";
    public const string Basic = "pri_basic_month", BasicYear = "pri_basic_year", Registers = "pri_registers_month", Invoices = "pri_invoices_month";

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
        builder.UseSetting("Billing:Paddle:Prices:Default:Monthly", Basic);
        builder.UseSetting("Billing:Paddle:Prices:Default:Annual", BasicYear);
        builder.UseSetting("Billing:Paddle:Prices:MultiRegister:Monthly", Registers);
        builder.UseSetting("Billing:Paddle:Prices:SupplierInvoiceReading:Monthly", Invoices);
        builder.UseSetting("Billing:License:PrivateKey", LicenseKey.ExportPkcs8PrivateKeyPem());
        builder.ConfigureTestServices(s => s.AddHttpClient<PaddleApi>().ConfigurePrimaryHttpMessageHandler(() => Paddle));
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

public sealed class BillingTests(BillingApp app) : IClassFixture<BillingApp>
{
    private static readonly DateTime PeriodEnd = new(2026, 11, 15, 15, 0, 0, DateTimeKind.Utc);

    private static object Subscription(string eventId, string type, Guid tenant, string status, DateTime occurred, DateTime periodEnd, params string[] prices) => new
    {
        event_id = eventId, event_type = type, occurred_at = occurred.ToString("O"), notification_id = "ntf_" + eventId,
        data = new
        {
            id = "sub_" + tenant.ToString("N"), status, customer_id = "ctm_" + tenant.ToString("N"),
            items = prices.Select(p => new { quantity = 1, price = new { id = p } }),
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
        var owner = await own.OwnerAsync();
        var node = await own.StoreWithNodeAsync(owner, "Carnicería La Esquina");
        async Task<string?> LicenseAsync() => (await (await node.Client.PostAsync("/api/nodes/license", null)).Json()).GetProperty("license").GetString();
        (await LicenseAsync()).ShouldBeNull(); // nothing paid

        (await own.SendAsync(Subscription(Id(), "subscription.created", owner.TenantId, "active", own.Now, PeriodEnd, BillingApp.Registers, BillingApp.Invoices))).EnsureSuccessStatusCode();
        var jws = await LicenseAsync();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Jws.Verify(jws!, other).IsFailure.ShouldBeTrue();
        var license = Jws.Verify(jws!, other, own.LicenseKey).Value; // the store trusts either key
        (license.Store, license.Plan, license.Limits["Registers"], license.ExpiresUtc).ShouldBe(("Carnicería La Esquina", "Varias cajas", 3, PeriodEnd.AddDays(3)));
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
        Jws.Verify((await LicenseAsync())!, own.LicenseKey).Value.ExpiresUtc.ShouldBe(PeriodEnd.AddDays(3)); // then the store's grace, then Restricted

        // Past the margin there is no license to hand out; the store's own grace and Restricted follow (never read-only).
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
        var node = await app.StoreWithNodeAsync(owner);
        (await (await node.Client.PostAsync("/api/nodes/license", null)).Json()).GetProperty("license").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
