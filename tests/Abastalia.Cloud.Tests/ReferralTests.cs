using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Host.Cloud.Billing;
using Abastalia.Host.Cloud.Fe;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-255: "Recomiende Abastalia". A referral is a discount on the referrer's next payment, earned when the referred business pays a qualifying
/// plan (MultiRegister in <see cref="BillingApp"/>, 10 %, 3 a month). Paddle is <see cref="FakePaddle"/>: no live calls.</summary>
public sealed class ReferralTests(BillingApp app) : IClassFixture<BillingApp>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await app.SyncCatalogAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly DateTime PeriodEnd = new(2026, 11, 15, 15, 0, 0, DateTimeKind.Utc);

    private DateTime Now => app.Now;

    private static string Id() => "evt_" + Guid.NewGuid().ToString("N");

    private static string Sub(Guid tenant) => "sub_" + tenant.ToString("N");

    private Task<HttpResponseMessage> SubscriptionAsync(Guid tenant, string status, DateTime occurred, DateTime? nextBilled = null, string interval = "month") => app.SendAsync(new
    {
        event_id = Id(), event_type = "subscription.updated", occurred_at = occurred.ToString("O"),
        data = new
        {
            id = Sub(tenant), status, customer_id = "ctm_" + tenant.ToString("N"),
            items = new[] { new { quantity = 1, price = new { id = BillingApp.Registers } } },
            current_billing_period = new { starts_at = (nextBilled ?? PeriodEnd).AddMonths(-1).ToString("O"), ends_at = (nextBilled ?? PeriodEnd).ToString("O") },
            next_billed_at = status == "active" ? (nextBilled ?? PeriodEnd).ToString("O") : null,
            billing_cycle = new { interval, frequency = 1 },
            custom_data = BillingApp.CustomData(tenant), // R12 I: signed, as our checkout makes it
        },
    });

    private async Task<string> PayAsync(Guid tenant, string price = BillingApp.Registers, string total = "2000", string? customer = null, string? eventId = null, string? txn = null)
    {
        app.Clock.Advance(TimeSpan.FromSeconds(1)); // each payment its own instant: the oldest pending reward goes first
        txn ??= "txn_" + Guid.NewGuid().ToString("N");
        (await app.SendAsync(new
        {
            event_id = eventId ?? Id(), event_type = "transaction.completed", occurred_at = Now.ToString("O"),
            data = new
            {
                id = txn, status = "completed", customer_id = customer ?? "ctm_" + tenant.ToString("N"), subscription_id = Sub(tenant), origin = "web",
                items = new[] { new { price = new { id = price }, quantity = 1 } },
                details = new { totals = new { grand_total = total, currency_code = "USD" } },
                billing_period = new { starts_at = Now.ToString("O"), ends_at = PeriodEnd.ToString("O") },
                custom_data = BillingApp.CustomData(tenant), // R12 I: signed, as our checkout makes it
            },
        })).EnsureSuccessStatusCode();
        return txn;
    }

    private static async Task<JsonElement> ReferralsAsync(Owner owner) => await owner.Client.GetFromJsonAsync<JsonElement>("/api/billing/referrals");

    private static async Task<string> CodeAsync(Owner owner) => (await ReferralsAsync(owner)).GetProperty("code").GetString()!;

    /// <summary>A new business that came with <paramref name="code"/> and opened the checkout (the panel sends the code it kept from /precios).</summary>
    private async Task<(Owner Owner, JsonElement CustomData)> ReferredAsync(string code)
    {
        var owner = await app.OwnerAsync();
        var checkout = await (await owner.Client.PostAsJsonAsync("/api/billing/checkout", new { plan = "MultiRegister", interval = "Monthly", referralCode = code.ToLowerInvariant() })).Json();
        return (owner, checkout.GetProperty("customData"));
    }

    private Task<List<Referral>> RowsAsync(Guid referrer) => app.InDbAsync(db => db.Referrals.AsNoTracking().Where(r => r.ReferrerTenantId == referrer).OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).ToListAsync());

    /// <summary>Paddle calls for the referrer: its subscription, and the discounts made for its referrals.</summary>
    private async Task<List<(HttpMethod Method, string Path, string? Authorization, JsonElement Body)>> CallsAsync(Guid referrer)
    {
        var ids = (await RowsAsync(referrer)).Select(r => r.Id.ToString()).ToHashSet();
        lock (app.Paddle.Requests)
            return [.. app.Paddle.Requests.Where(r => r.Path.StartsWith("/subscriptions/" + Sub(referrer), StringComparison.Ordinal)
                || r.Path == "/discounts" && ids.Contains(r.Body.GetProperty("custom_data").GetProperty("abastalia_referral_id").GetString()!))];
    }

    [Fact]
    public async Task A_paid_referral_lowers_the_referrers_next_payment_once()
    {
        var referrer = await app.OwnerAsync();
        var mine = await ReferralsAsync(referrer);
        (mine.GetProperty("enabled").GetBoolean(), mine.GetProperty("pending").GetInt32(), mine.GetProperty("reward").GetProperty("amount").GetString()).ShouldBe((true, 0, "10"));
        var code = mine.GetProperty("code").GetString()!;
        code.Length.ShouldBe(8);
        (await CodeAsync(referrer)).ShouldBe(code); // made once
        (await app.CreateClient().GetAsync($"/api/public/referrals/{code}")).StatusCode.ShouldBe(HttpStatusCode.OK); // /precios?ref= checks it, no account
        (await SubscriptionAsync(referrer.TenantId, "active", Now)).EnsureSuccessStatusCode();

        var (referred, customData) = await ReferredAsync(code);
        (customData.GetProperty("referral_code").GetString(), customData.GetProperty("referrer_tenant_id").GetGuid()).ShouldBe((code, referrer.TenantId));
        (await RowsAsync(referrer.TenantId)).ShouldBeEmpty(); // signing up earns nothing

        var eventId = Id();
        await PayAsync(referred.TenantId, eventId: eventId); // the referred business pays "Varias cajas"
        var rows = await RowsAsync(referrer.TenantId);
        rows.Select(r => (r.Status, r.ReferredTenantId, r.DiscountFromUtc)).ShouldBe([(ReferralStatus.Applied, referred.TenantId, PeriodEnd)]);
        var calls = (await CallsAsync(referrer.TenantId));
        var discount = calls.Single(c => c.Path == "/discounts").Body;
        (discount.GetProperty("type").GetString(), discount.GetProperty("amount").GetString(), discount.GetProperty("recur").GetBoolean(),
            discount.GetProperty("usage_limit").GetInt32(), discount.GetProperty("mode").GetString()).ShouldBe(("percentage", "10", false, 1, "custom"));
        discount.GetProperty("restrict_to").EnumerateArray().Select(p => p.GetString()).ShouldBe([BillingApp.Registers]); // the plan, not the add-on
        discount.TryGetProperty("currency_code", out _).ShouldBeFalse();
        calls.Where(c => c.Method == HttpMethod.Patch).Select(c => c.Path).ShouldBe([$"/subscriptions/{Sub(referrer.TenantId)}/preview", $"/subscriptions/{Sub(referrer.TenantId)}"]);
        var set = calls.Last().Body.GetProperty("discount");
        (set.GetProperty("id").GetString(), set.GetProperty("effective_from").GetString()).ShouldBe((rows[0].PaddleDiscountId, "next_billing_period"));

        mine = await ReferralsAsync(referrer);
        (mine.GetProperty("applied").GetInt32(), mine.GetProperty("nextDiscount").GetProperty("fromUtc").GetDateTime()).ShouldBe((1, PeriodEnd));

        // The same event again, and the referred business's next payments: one reward per referred business, ever.
        await PayAsync(referred.TenantId, eventId: eventId);
        await PayAsync(referred.TenantId);
        (await RowsAsync(referrer.TenantId)).Count.ShouldBe(1);
        (await CallsAsync(referrer.TenantId)).Count(c => c.Path == "/discounts").ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_first_paid_transaction_of_a_qualifying_plan_counts()
    {
        var referrer = await app.OwnerAsync();
        (await SubscriptionAsync(referrer.TenantId, "active", Now)).EnsureSuccessStatusCode();
        var code = await CodeAsync(referrer);

        var basic = (await ReferredAsync(code)).Owner;
        await PayAsync(basic.TenantId, BillingApp.Basic); // "Una caja" does not qualify...
        await PayAsync(basic.TenantId);                    // ...and an upgrade later is not its first payment
        (await RowsAsync(referrer.TenantId)).ShouldBeEmpty();

        var trial = (await ReferredAsync(code)).Owner;
        await PayAsync(trial.TenantId, total: "0"); // the trial's checkout: nothing paid yet
        (await RowsAsync(referrer.TenantId)).ShouldBeEmpty();
        await PayAsync(trial.TenantId);
        (await RowsAsync(referrer.TenantId)).Single().ReferredTenantId.ShouldBe(trial.TenantId);
    }

    [Fact]
    public async Task Self_referral_is_blocked_by_business_payer_and_RNC()
    {
        var referrer = await app.OwnerAsync();
        var code = await CodeAsync(referrer);
        // Its own link: the checkout does not keep it.
        var own = await (await referrer.Client.PostAsJsonAsync("/api/billing/checkout", new { plan = "MultiRegister", referralCode = code })).Json();
        own.GetProperty("customData").TryGetProperty("referral_code", out _).ShouldBeFalse();
        (await SubscriptionAsync(referrer.TenantId, "active", Now)).EnsureSuccessStatusCode();

        var samePayer = (await ReferredAsync(code)).Owner;
        await PayAsync(samePayer.TenantId, customer: "ctm_" + referrer.TenantId.ToString("N")); // the referrer's own Paddle customer (same e-mail)

        var sameRnc = (await ReferredAsync(code)).Owner;
        await app.InDbAsync(async db =>
        {
            db.FeCertificates.AddRange(new FeCertificate { Id = Guid.NewGuid(), TenantId = referrer.TenantId, Rnc = "131000001", RevokedUtc = Now }, // its receiver, given up
                new FeCertificate { Id = Guid.NewGuid(), TenantId = sameRnc.TenantId, Rnc = "131000001" });
            return await db.SaveChangesAsync();
        });
        await PayAsync(sameRnc.TenantId);

        (await RowsAsync(referrer.TenantId)).Select(r => (r.Status, r.Reason)).ShouldBe([(ReferralStatus.Rejected, "SamePayer"), (ReferralStatus.Rejected, "SameRnc")]);
        (await CallsAsync(referrer.TenantId)).ShouldNotContain(c => c.Path == "/discounts");
    }

    [Fact]
    public async Task A_reward_waits_while_the_referrer_is_past_due_or_already_discounted_and_goes_one_per_cycle()
    {
        var referrer = await app.OwnerAsync();
        var code = await CodeAsync(referrer);
        (await SubscriptionAsync(referrer.TenantId, "past_due", Now)).EnsureSuccessStatusCode();

        var first = (await ReferredAsync(code)).Owner;
        await PayAsync(first.TenantId);
        (await RowsAsync(referrer.TenantId)).Single().Status.ShouldBe(ReferralStatus.Pending); // past due: queued
        (await CallsAsync(referrer.TenantId)).ShouldNotContain(c => c.Path == "/discounts");

        (await SubscriptionAsync(referrer.TenantId, "active", Now.AddMinutes(1))).EnsureSuccessStatusCode(); // the card went through
        (await RowsAsync(referrer.TenantId)).Single().Status.ShouldBe(ReferralStatus.Applied);

        var second = (await ReferredAsync(code)).Owner;
        await PayAsync(second.TenantId);
        (await RowsAsync(referrer.TenantId)).Select(r => r.Status).ShouldBe([ReferralStatus.Applied, ReferralStatus.Pending]); // one per payment

        // Billed: Paddle moved to the next period, but our discount is still on the subscription there: it waits.
        var next = PeriodEnd.AddMonths(1);
        (await SubscriptionAsync(referrer.TenantId, "active", Now.AddMinutes(2), next)).EnsureSuccessStatusCode();
        (await RowsAsync(referrer.TenantId))[1].Status.ShouldBe(ReferralStatus.Pending);
        lock (app.Paddle.Discounts) app.Paddle.Discounts[Sub(referrer.TenantId)] = null; // used up
        (await SubscriptionAsync(referrer.TenantId, "active", Now.AddMinutes(3), next)).EnsureSuccessStatusCode();
        (await RowsAsync(referrer.TenantId))[1].DiscountFromUtc.ShouldBe(next);
    }

    [Fact]
    public async Task A_refund_of_the_referred_payment_takes_the_unbilled_discount_back()
    {
        var referrer = await app.OwnerAsync();
        (await SubscriptionAsync(referrer.TenantId, "active", Now)).EnsureSuccessStatusCode();
        var referred = (await ReferredAsync(await CodeAsync(referrer))).Owner;
        var txn = await PayAsync(referred.TenantId);
        (await RowsAsync(referrer.TenantId)).Single().Status.ShouldBe(ReferralStatus.Applied);

        Task<HttpResponseMessage> Adjustment(string status) => app.SendAsync(new
        {
            event_id = Id(), event_type = "adjustment.updated", occurred_at = Now.ToString("O"),
            data = new { id = "adj_1", action = "refund", status, transaction_id = txn, customer_id = "ctm_" + referred.TenantId.ToString("N") },
        });
        (await Adjustment("pending_approval")).EnsureSuccessStatusCode();
        (await RowsAsync(referrer.TenantId)).Single().Status.ShouldBe(ReferralStatus.Applied); // asked, not approved

        (await Adjustment("approved")).EnsureSuccessStatusCode();
        (await RowsAsync(referrer.TenantId)).Single().ShouldSatisfyAllConditions(r => r.Status.ShouldBe(ReferralStatus.Reverted), r => r.Reason.ShouldBe("Refund"));
        var removed = (await CallsAsync(referrer.TenantId)).Last();
        (removed.Method, removed.Path, removed.Body.GetProperty("discount").ValueKind).ShouldBe((HttpMethod.Patch, $"/subscriptions/{Sub(referrer.TenantId)}", JsonValueKind.Null));
        (await ReferralsAsync(referrer)).GetProperty("nextDiscount").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_referrer_earns_at_most_the_monthly_limit()
    {
        var referrer = await app.OwnerAsync();
        (await SubscriptionAsync(referrer.TenantId, "active", Now)).EnsureSuccessStatusCode();
        var code = await CodeAsync(referrer);
        for (var i = 0; i < 4; i++) await PayAsync((await ReferredAsync(code)).Owner.TenantId);

        (await RowsAsync(referrer.TenantId)).Select(r => r.Status).ShouldBe([ReferralStatus.Applied, ReferralStatus.Pending, ReferralStatus.Pending, ReferralStatus.Rejected]);
        (await RowsAsync(referrer.TenantId)).Last().Reason.ShouldBe("MonthlyLimit");
        var mine = await ReferralsAsync(referrer);
        (mine.GetProperty("pending").GetInt32(), mine.GetProperty("applied").GetInt32()).ShouldBe((2, 1));
    }

    [Fact]
    public async Task Staff_see_the_referrals_and_adjust_them_with_a_reason_audited()
    {
        var referrer = await app.OwnerAsync();
        (await SubscriptionAsync(referrer.TenantId, "active", Now)).EnsureSuccessStatusCode();
        var referred = (await ReferredAsync(await CodeAsync(referrer))).Owner;
        await PayAsync(referred.TenantId);
        var id = (await RowsAsync(referrer.TenantId)).Single().Id;
        var path = $"/api/platform/tenants/{referrer.TenantId}/referrals";
        (await referrer.Client.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var staff = await app.StaffAsync();
        var view = await staff.Client.GetFromJsonAsync<JsonElement>(path);
        view.GetProperty("code").GetString().ShouldBe(await CodeAsync(referrer));
        var row = view.GetProperty("referrals").EnumerateArray().Single();
        (row.GetProperty("status").GetString(), row.GetProperty("referredName").GetString()).ShouldBe(("Applied", referred.Email));

        (await staff.Client.PostAsJsonAsync($"{path}/{id}", new { action = "Revert" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // the reason is required
        (await staff.Client.PostAsJsonAsync($"{path}/{id}", new { action = "Revert", note = "Cliente pidió reembolso por WhatsApp" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RowsAsync(referrer.TenantId)).Single().ShouldSatisfyAllConditions(r => r.Status.ShouldBe(ReferralStatus.Reverted), r => r.Note.ShouldBe("Cliente pidió reembolso por WhatsApp"));
        (await staff.Client.PostAsJsonAsync($"{path}/{id}", new { action = "Retry" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // not pending

        (await staff.Client.PostAsJsonAsync($"{path}/{id}", new { action = "Reinstate", note = "Error nuestro" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RowsAsync(referrer.TenantId)).Single().Status.ShouldBe(ReferralStatus.Applied); // pending again, and applied at once (its discount was taken off)
        (await app.InDbAsync(db => db.PlatformAudit.CountAsync(a => a.TenantId == referrer.TenantId && a.Method == "POST" && a.Path.Contains("/referrals/")))).ShouldBe(4);
    }

    [Fact]
    public async Task An_annual_referrer_gets_its_rewards_together_on_the_renewal()
    {
        var referrer = await app.OwnerAsync();
        (await SubscriptionAsync(referrer.TenantId, "active", Now, interval: "year")).EnsureSuccessStatusCode();
        var code = await CodeAsync(referrer);
        var txns = new List<string>();
        for (var i = 0; i < 3; i++) txns.Add(await PayAsync((await ReferredAsync(code)).Owner.TenantId));

        // Each new reward replaced the renewal's discount with one for all of them: 10 %, 20 %, 30 %.
        var rows = await RowsAsync(referrer.TenantId);
        rows.ShouldAllBe(r => r.Status == ReferralStatus.Applied && r.DiscountFromUtc == PeriodEnd);
        rows.Select(r => r.PaddleDiscountId).Distinct().Count().ShouldBe(1);
        var discounts = (await CallsAsync(referrer.TenantId)).Where(c => c.Path == "/discounts").Select(c => c.Body).ToList();
        discounts.Select(d => d.GetProperty("amount").GetString()).ShouldBe(["10", "20", "30"]);
        discounts[2].GetProperty("description").GetString().ShouldBe("Abastalia: 3 recomendaciones");
        lock (app.Paddle.Discounts) app.Paddle.Discounts[Sub(referrer.TenantId)].ShouldBe(rows[0].PaddleDiscountId);

        // A refund of one: the combined discount comes off and the other two go back on together.
        (await app.SendAsync(new
        {
            event_id = Id(), event_type = "adjustment.created", occurred_at = Now.ToString("O"),
            data = new { id = "adj_annual", action = "refund", status = "approved", transaction_id = txns[1] },
        })).EnsureSuccessStatusCode();
        rows = await RowsAsync(referrer.TenantId);
        rows.Select(r => r.Status).ShouldBe([ReferralStatus.Applied, ReferralStatus.Reverted, ReferralStatus.Applied]);
        (await CallsAsync(referrer.TenantId)).Last(c => c.Path == "/discounts").Body.GetProperty("amount").GetString().ShouldBe("20");
        lock (app.Paddle.Discounts) app.Paddle.Discounts[Sub(referrer.TenantId)].ShouldBe(rows[0].PaddleDiscountId);
    }

    [Fact]
    public void Annual_rewards_are_capped_at_half_the_plan()
    {
        static Referral Flat(int i) => new() { Id = Guid.NewGuid(), DiscountType = "flat", DiscountAmount = "1500", Currency = "USD", CreatedUtc = DateTime.UnixEpoch.AddDays(i) };
        var six = Enumerable.Range(0, 6).Select(Flat).ToList();

        var (batch, amount) = ReferralRewards.Combine([], six, planTotal: 15000, capPercent: 50); // Esencial anual, US$150: at most US$75
        (batch.Count, amount).ShouldBe((5, "7500"));
        (batch, amount) = ReferralRewards.Combine(six[..5], [six[5]], 15000, 50); // the renewal is full: the sixth waits
        batch.Count.ShouldBe(5);
        (batch, amount) = ReferralRewards.Combine([], six, planTotal: 2000, capPercent: 50); // one reward above the cap goes alone, cut to it
        (batch.Count, amount).ShouldBe((1, "1000"));
        (batch, amount) = ReferralRewards.Combine([], six, planTotal: null, capPercent: 50); // plan price unknown: one at a time
        (batch.Count, amount).ShouldBe((1, "1500"));
    }

    [Fact]
    public async Task A_business_that_came_with_a_code_gets_a_30_day_store_trial()
    {
        var referrer = await app.OwnerAsync();
        var code = await CodeAsync(referrer);
        var referred = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(referred, "Colmado Recomendado");
        async Task<JsonElement> TrialAsync() => (await (await node.Client.PostAsync("/api/nodes/license", null)).Json()).GetProperty("trial");

        (await TrialAsync()).ValueKind.ShouldBe(JsonValueKind.Null); // the store's own 14
        (await referred.Client.PostAsJsonAsync("/api/billing/referrals/claim", new { code = code.ToLowerInvariant() })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // R12 #7 (TK-308): a grant signed with the license key, for this business only, never a bare number.
        var grant = Jws.Verify<TrialGrant>((await TrialAsync()).GetString()!, Jws.TrialType, [app.LicenseKey]).Value;
        (grant.TenantId, grant.Days, grant.IssuedUtc).ShouldBe((referred.TenantId, 30, Now));
        Jws.Verify((await TrialAsync()).GetString()!, app.LicenseKey).IsFailure.ShouldBeTrue(); // no license either

        // Its own code, or a business older than two weeks, keeps nothing.
        var own = await app.OwnerAsync();
        (await own.Client.PostAsJsonAsync("/api/billing/referrals/claim", new { code = await CodeAsync(own) })).EnsureSuccessStatusCode();
        var old = await app.OwnerAsync();
        await app.InDbAsync(db => db.Tenants.Where(t => t.Id == old.TenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedUtc, Now.AddDays(-15))));
        (await old.Client.PostAsJsonAsync("/api/billing/referrals/claim", new { code })).EnsureSuccessStatusCode();
        // R12 #10: nor does its checkout (the code there gives the same 30-day trial).
        var checkout = await (await old.Client.PostAsJsonAsync("/api/billing/checkout", new { plan = "MultiRegister", interval = "Monthly", referralCode = code })).Json();
        checkout.GetProperty("customData").TryGetProperty("referral_code", out _).ShouldBeFalse();
        (await app.InDbAsync(db => db.TenantSubscriptions.Where(s => s.TenantId == own.TenantId || s.TenantId == old.TenantId).Select(s => s.ReferredByCode).ToListAsync()))
            .ShouldAllBe(c => c == null);
    }

    [Fact]
    public async Task R12_10_the_cloud_gives_a_referred_business_the_same_30_day_trial_for_the_paid_extras()
    {
        var code = await CodeAsync(await app.OwnerAsync());
        var referred = await app.OwnerAsync();
        (await referred.Client.PostAsJsonAsync("/api/billing/referrals/claim", new { code })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var plain = await app.OwnerAsync();
        foreach (var who in new[] { referred, plain })
        {
            var node = await app.StoreWithNodeAsync(who);
            var sale = new Abastalia.Sales.Contracts.SaleRecordedV2(Guid.CreateVersion7(), 1, Guid.NewGuid(), Guid.NewGuid(), null, "E320000000001", 100m, 0m, 0m,
                [new Abastalia.Sales.Contracts.SaleLineV2(Guid.NewGuid(), null, "Pollo", 1, "Unit", 100m, 0m, 50m)], [new Abastalia.Sales.Contracts.SalePaymentV1("Cash", 100m)]);
            (await node.SendAsync(Events.Envelope(sale, Now.AddDays(-20)))).EnsureSuccessStatusCode(); // first sale 20 days ago
        }
        await app.ApplyAsync();
        var check = app.Services.GetRequiredService<Abastalia.Host.Cloud.Assistant.LicenseCheck>();
        (await app.InDbAsync(db => check.AllowsPaidExtrasAsync(db, referred.TenantId, CancellationToken.None))).ShouldBeTrue();
        (await app.InDbAsync(db => check.AllowsPaidExtrasAsync(db, plain.TenantId, CancellationToken.None))).ShouldBeFalse(); // 14 days
    }

    [Fact]
    public async Task R12_10_reinstating_starts_clean_and_a_combined_discount_shows_its_whole_amount()
    {
        var referrer = await app.OwnerAsync();
        (await SubscriptionAsync(referrer.TenantId, "active", Now, interval: "year")).EnsureSuccessStatusCode();
        var code = await CodeAsync(referrer);
        for (var i = 0; i < 2; i++) await PayAsync((await ReferredAsync(code)).Owner.TenantId);
        var next = (await ReferralsAsync(referrer)).GetProperty("nextDiscount");
        (next.GetProperty("amount").GetString(), next.GetProperty("count").GetInt32()).ShouldBe(("20", 2)); // 10 % + 10 %, as Paddle has it

        // A monthly referrer's reverted reward, reinstated: a new discount, not the one taken off.
        var monthly = await app.OwnerAsync();
        (await SubscriptionAsync(monthly.TenantId, "active", Now)).EnsureSuccessStatusCode();
        await PayAsync((await ReferredAsync(await CodeAsync(monthly))).Owner.TenantId);
        var row = (await RowsAsync(monthly.TenantId)).Single();
        var staff = await app.StaffAsync();
        var path = $"/api/platform/tenants/{monthly.TenantId}/referrals/{row.Id}";
        (await staff.Client.PostAsJsonAsync(path, new { action = "Revert", note = "Prueba" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await staff.Client.PostAsJsonAsync(path, new { action = "Reinstate", note = "Error nuestro" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var again = (await RowsAsync(monthly.TenantId)).Single();
        again.Status.ShouldBe(ReferralStatus.Applied);
        again.PaddleDiscountId.ShouldNotBe(row.PaddleDiscountId);
    }

    [Fact]
    public async Task Code_lookups_are_limited_per_IP()
    {
        await using var own = new BillingApp(); // its own limiter: 5 a minute
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) statuses.Add((await own.CreateClient().GetAsync("/api/public/referrals/ABCDEFGH")).StatusCode);
        statuses.ShouldBe([.. Enumerable.Repeat(HttpStatusCode.NotFound, 5), HttpStatusCode.TooManyRequests]);
    }
}
