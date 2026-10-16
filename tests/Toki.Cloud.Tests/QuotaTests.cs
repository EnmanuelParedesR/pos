using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Host.Cloud.Assistant;
using Toki.Host.Cloud.Quotas;

namespace Toki.Cloud.Tests;

/// <summary>The invoice reader's fake for invoices; any other call (the assistant) answers at once and reports 700 tokens.</summary>
public sealed class QuotaModel : IChatModel
{
    private readonly FakeInvoiceModel _invoices = new();

    public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct) =>
        tools.Any(t => t.Name == SupplierInvoiceReader.ToolName)
            ? _invoices.CompleteAsync(messages, tools, ct)
            : Task.FromResult(new ChatMessage("assistant", "Vendió RD$ 1,000.00.", Tokens: 700));
}

/// <summary>Small monthly limits so the tests reach them (TK-248).</summary>
public sealed class QuotaApp() : CloudApp(new QuotaModel())
{
    public const int Invoices = 2, AddOnInvoices = 5, Invites = 2, Questions = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Quotas:Default:SupplierInvoices", $"{Invoices}");
        builder.UseSetting("Quotas:Default:Invites", $"{Invites}");
        builder.UseSetting("Quotas:Default:AssistantQuestions", $"{Questions}");
        builder.UseSetting("Quotas:Default:AssistantTokens", "1000");
        builder.UseSetting("Quotas:MultiStore:Invites", "40");
        builder.UseSetting("Quotas:AddOns:SupplierInvoiceReading:SupplierInvoices", $"{AddOnInvoices}");
    }
}

public sealed class QuotaTests(QuotaApp app) : IClassFixture<QuotaApp>
{
    private static object Photo() => new { file = Convert.ToBase64String([0xFF, 0xD8, 0xFF, 1, 2, 3]), contentType = "image/jpeg", fileName = "factura.jpg" };

    private static Task<HttpResponseMessage> InviteAsync(Owner owner) =>
        owner.Client.PostAsJsonAsync("/api/members/invites", new { email = $"x{Guid.NewGuid():N}@example.com", role = "ReadOnly" });

    private async Task<QuotaResult> ConsumeAsync(Guid tenant, Meter meter, int n = 1)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQuota>().TryConsumeAsync(tenant, meter, n);
    }

    [Fact]
    public async Task Calls_at_once_never_take_more_than_the_limit()
    {
        var owner = await app.OwnerAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => ConsumeAsync(owner.TenantId, Meter.Invites))));

        results.Count(r => r.Allowed).ShouldBe(QuotaApp.Invites);
        (await app.InDbAsync(db => Task.FromResult(db.TenantUsage.Single(u => u.TenantId == owner.TenantId && u.Meter == Meter.Invites).Count))).ShouldBe(QuotaApp.Invites);
    }

    [Fact]
    public async Task At_the_limit_the_API_answers_429_with_the_reset_date_and_takes_nothing_more()
    {
        var owner = await app.OwnerAsync();
        for (var i = 0; i < QuotaApp.Invites; i++) (await InviteAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var over = await InviteAsync(owner);

        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var problem = await over.Json();
        problem.GetProperty("code").GetString().ShouldBe("Quota.Exceeded");
        problem.GetProperty("meter").GetString().ShouldBe("Invites");
        problem.GetProperty("resetsOn").GetString().ShouldBe("2026-11-01");
        problem.GetProperty("detail").GetString()!.ShouldContain("01/11/2026");
        var usage = await owner.Client.GetFromJsonAsync<JsonElement>("/api/usage");
        Used(usage, "Invites").ShouldBe((QuotaApp.Invites, QuotaApp.Invites));
        Used(usage, "Emails").Used.ShouldBe(QuotaApp.Invites + 1); // the invites' e-mails and the registration's confirmation
    }

    [Fact]
    public async Task A_new_store_month_starts_from_zero()
    {
        await using var fresh = new QuotaApp();
        var owner = await fresh.OwnerAsync();
        for (var i = 0; i < QuotaApp.Invites; i++) (await InviteAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await InviteAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        fresh.Clock.Advance(TimeSpan.FromDays(16) + TimeSpan.FromHours(12)); // 1 Nov 03:00 UTC is still 31 Oct in the store (UTC−4)
        owner = await fresh.SignInAsync(owner.Email); // the old token expired on the fake clock
        (await InviteAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        fresh.Clock.Advance(TimeSpan.FromHours(2)); // 1 Nov 01:00 in the store
        owner = await fresh.SignInAsync(owner.Email);

        (await InviteAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/usage")).GetProperty("resetsOn").GetString().ShouldBe("2026-12-01");
    }

    [Fact]
    public async Task The_plan_set_in_the_console_brings_its_limits_and_falls_back_to_Default()
    {
        var owner = await app.OwnerAsync();
        var staff = await app.StaffAsync();
        var start = await staff.Client.GetFromJsonAsync<JsonElement>($"/api/platform/tenants/{owner.TenantId}/usage");
        start.GetProperty("usage").GetProperty("plan").GetString().ShouldBe("Default");
        start.GetProperty("plans").EnumerateArray().Select(p => p.GetString()).ShouldBe(["Default", "Essential", "MultiRegister", "MultiStore"]);
        start.GetProperty("addOns").EnumerateArray().Select(p => p.GetString()).ShouldBe(["AssistantPack", "ExtraRegister", "ExtraStore", "ExtraUsers", "SupplierInvoiceReading"]);

        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/plan", new { plan = "Gold", addOns = Array.Empty<string>() }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/plan", new { plan = "MultiStore", addOns = Array.Empty<string>() }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/plan", new { plan = "MultiStore" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var usage = await owner.Client.GetFromJsonAsync<JsonElement>("/api/usage");
        usage.GetProperty("plan").GetString().ShouldBe("MultiStore");
        Used(usage, "Invites").Limit.ShouldBe(40);                     // the plan's own
        Used(usage, "SupplierInvoices").Limit.ShouldBe(QuotaApp.Invoices); // not set for the plan: Default's

        // TK-258: add-ons add to the plan, once per unit (a code twice = two packs).
        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/plan", new { plan = "Essential", addOns = new[] { "AssistantPack", "AssistantPack", "ExtraUsers" } }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        usage = await owner.Client.GetFromJsonAsync<JsonElement>("/api/usage");
        usage.GetProperty("addOns").EnumerateArray().Select(a => a.GetString()).ShouldBe(["AssistantPack", "AssistantPack", "ExtraUsers"]);
        Used(usage, "AssistantQuestions").Limit.ShouldBe(100 + 2 * 1000);
        Used(usage, "Invites").Limit.ShouldBe(2 + 5);
    }

    [Fact]
    public async Task The_invoice_reading_add_on_raises_the_month_and_the_store_sees_what_is_left()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        for (var i = 0; i < QuotaApp.Invoices; i++) (await node.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var over = await node.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo());
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await over.Json()).GetProperty("detail").GetString()!.ShouldContain("adicional de lectura de facturas");
        var left = await (await node.Client.PostAsJsonAsync("/ai/v1/supplier-invoice/quota", new { })).Json();
        (left.GetProperty("used").GetInt32(), left.GetProperty("limit").GetInt32(), left.GetProperty("addOn").GetBoolean()).ShouldBe((QuotaApp.Invoices, QuotaApp.Invoices, false));

        var staff = await app.StaffAsync();
        (await staff.Client.PutAsJsonAsync($"/api/platform/tenants/{owner.TenantId}/plan", new { plan = "Default", addOns = new[] { "SupplierInvoiceReading" } }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await node.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo())).StatusCode.ShouldBe(HttpStatusCode.OK);
        left = await (await node.Client.PostAsJsonAsync("/ai/v1/supplier-invoice/quota", new { })).Json();
        (left.GetProperty("used").GetInt32(), left.GetProperty("limit").GetInt32(), left.GetProperty("addOn").GetBoolean()).ShouldBe((QuotaApp.Invoices + 1, QuotaApp.Invoices + QuotaApp.AddOnInvoices, true)); // the add-on adds its own
    }

    [Fact]
    public async Task The_assistant_counts_questions_and_the_tokens_the_model_reports()
    {
        var owner = await app.OwnerAsync();
        for (var i = 0; i < 2; i++)
            (await owner.Client.PostAsJsonAsync("/api/assistant/ask", new { question = "¿Cuánto vendí hoy?" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var usage = await owner.Client.GetFromJsonAsync<JsonElement>("/api/usage");
        Used(usage, "AssistantQuestions").Used.ShouldBe(2);
        Used(usage, "AssistantTokens").Used.ShouldBe(1400);
        var spent = await owner.Client.PostAsJsonAsync("/api/assistant/ask", new { question = "¿Y ayer?" }); // 1400 of 1000 tokens: the month is spent
        spent.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await spent.Json()).GetProperty("meter").GetString().ShouldBe("AssistantTokens");
    }

    [Fact]
    public async Task Sign_in_registration_and_invite_links_are_limited_per_IP()
    {
        await using var strict = app.WithWebHostBuilder(b => b.UseSetting("Auth:RequestsPerMinute", "2"));
        var client = strict.CreateClient();
        for (var i = 0; i < 2; i++)
            (await client.PostAsJsonAsync("/api/auth/login", new { email = "nadie@example.com", password = "Incorrecta1" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/auth/login", new { email = "nadie@example.com", password = "Incorrecta1" })).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.PostAsJsonAsync("/api/auth/forgotPassword", new { email = "nadie@example.com" })).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.PostAsJsonAsync("/api/invites/preview", new { token = "x" })).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_panel_is_limited_per_user()
    {
        await using var strict = app.WithWebHostBuilder(b => b.UseSetting("Panel:RequestsPerMinutePerUser", "2"));
        var client = strict.CreateClient();
        var email = $"owner{Guid.NewGuid():N}@example.com";
        (await client.PostAsJsonAsync("/api/auth/register", new { email, password = CloudApp.Password })).EnsureSuccessStatusCode();
        await CloudApp.ConfirmEmailAsync(strict, email);
        var token = (await (await client.PostAsJsonAsync("/api/auth/login", new { email, password = CloudApp.Password })).Json()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        for (var i = 0; i < 2; i++) (await client.GetAsync("/api/panel/sync-status")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/panel/sync-status")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private static (int Used, int Limit) Used(JsonElement usage, string meter) =>
        usage.GetProperty("meters").EnumerateArray().Where(m => m.GetProperty("meter").GetString() == meter)
            .Select(m => (m.GetProperty("used").GetInt32(), m.GetProperty("limit").GetInt32())).Single();
}
