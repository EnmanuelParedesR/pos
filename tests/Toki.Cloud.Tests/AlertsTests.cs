using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.Fiscal.Contracts;
using Toki.Host.Cloud.Fe;
using Toki.Host.Cloud.Platform;
using Toki.Sync.Contracts;

namespace Toki.Cloud.Tests;

/// <summary>TK-222: e-mail alerts from what the stores upload. TK-225: certification progress in the console. TK-154: received e-CF in the panel.</summary>
public sealed class AlertsTests(MailingCloudApp app) : IClassFixture<MailingCloudApp>
{
    private DateTime Now => app.Clock.GetUtcNow().UtcDateTime;

    private static NodeHeartbeatV1 Heartbeat(NodeFiscalV1? fiscal) => PlatformTests.Heartbeat() with { Fiscal = fiscal };

    private List<(string To, string Subject, string Body)> Alerts(Owner who)
    {
        lock (app.Mail.Sent) return [.. app.Mail.Sent.Where(m => m.To == who.Email && m.Subject.StartsWith("Toki · ", StringComparison.Ordinal))];
    }

    private async Task<Owner> MemberAsync(Owner owner)
    {
        var email = $"contable{Guid.NewGuid():N}@example.com";
        (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" })).EnsureSuccessStatusCode();
        string body;
        lock (app.Mail.Sent) body = app.Mail.Sent.Single(m => m.To == email).Body;
        (await app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = body[(body.IndexOf('#') + 1)..].Trim(), password = CloudApp.Password }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return await app.SignInAsync(email);
    }

    [Fact]
    public async Task Fiscal_trouble_is_e_mailed_to_the_owners_once_per_day_and_never_to_read_only_members_by_default()
    {
        var owner = await app.OwnerAsync();
        var member = await MemberAsync(owner);
        var store = await app.StoreWithNodeAsync(owner);
        var trouble = new NodeFiscalV1(2, Now.AddHours(-30), Now.AddDays(10));

        (await store.SendAsync(Events.Envelope(Heartbeat(trouble), Now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();

        var sent = Alerts(owner);
        sent.Count.ShouldBe(3);
        sent.ShouldContain(m => m.Subject.Contains("Carnicería La Esquina") && m.Body.Contains("rechazó 2 comprobantes"));
        sent.ShouldContain(m => m.Body.Contains("vence el 25/10/2026"));
        sent.ShouldContain(m => m.Body.Contains("desde hace 30 horas"));
        sent.ShouldAllBe(m => m.Body.Contains("/unsubscribe#"));
        Alerts(member).ShouldBeEmpty();

        // The same trouble later the same day: nothing new. The next day: again.
        app.Clock.Advance(TimeSpan.FromHours(4));
        (await store.SendAsync(Events.Envelope(Heartbeat(trouble), Now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        Alerts(owner).Count.ShouldBe(3);

        app.Clock.Advance(TimeSpan.FromDays(1));
        (await store.SendAsync(Events.Envelope(Heartbeat(trouble), Now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        Alerts(owner).Count.ShouldBe(6);

        // A store on an older version sends no fiscal signals, a healthy one has nothing to say.
        var quiet = await app.OwnerAsync();
        var old = await app.StoreWithNodeAsync(quiet);
        (await old.SendAsync(Events.Envelope(Heartbeat(null), Now), Events.Envelope(Heartbeat(new NodeFiscalV1(0, Now.AddHours(-2), Now.AddDays(200))), Now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        Alerts(quiet).ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_member_chooses_their_alerts_and_the_e_mail_link_unsubscribes()
    {
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner);

        var mine = await owner.Client.GetFromJsonAsync<JsonElement>("/api/notifications");
        (mine.GetProperty("dgiiRejected").GetBoolean(), mine.GetProperty("unsubscribed").GetBoolean(), mine.GetProperty("emailDelivers").GetBoolean()).ShouldBe((true, false, true));
        var member = await MemberAsync(owner);
        (await member.Client.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("queueStale").GetBoolean().ShouldBeFalse();

        (await owner.Client.PutAsJsonAsync("/api/notifications", new { dgiiRejected = false, certificateExpiring = true, queueStale = true, certificationProgress = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await member.Client.PutAsJsonAsync("/api/notifications", new { dgiiRejected = true, certificateExpiring = false, queueStale = false, certificationProgress = false }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await store.SendAsync(Events.Envelope(Heartbeat(new NodeFiscalV1(1, null, Now.AddDays(5))), Now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();

        Alerts(owner).ShouldHaveSingleItem().Body.ShouldContain("certificado digital");
        var memberAlert = Alerts(member).ShouldHaveSingleItem();
        memberAlert.Body.ShouldContain("rechazó 1 comprobante electrónico");

        var token = memberAlert.Body[(memberAlert.Body.IndexOf('#') + 1)..].Trim();
        (await app.CreateClient().PostAsJsonAsync("/api/notifications/unsubscribe", new { token })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.CreateClient().PostAsJsonAsync("/api/notifications/unsubscribe", new { token = new string('0', 64) })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await member.Client.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("unsubscribed").GetBoolean().ShouldBeTrue();

        app.Clock.Advance(TimeSpan.FromDays(1));
        (await store.SendAsync(Events.Envelope(Heartbeat(new NodeFiscalV1(1, null, null)), Now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        Alerts(member).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Certification_progress_reaches_the_owner_and_the_console_with_the_stuck_list_and_the_funnel()
    {
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner, "Colmado Avance");
        var stuckOwner = await app.OwnerAsync();
        var stuck = await app.StoreWithNodeAsync(stuckOwner, "Colmado Parado");

        (await store.SendAsync(Events.Envelope(new CertificationProgressedV1([0, 1], CertificationSteps.Count, Now.AddDays(-2)), Now))).EnsureSuccessStatusCode();
        (await stuck.SendAsync(Events.Envelope(new CertificationProgressedV1([0], CertificationSteps.Count, Now.AddDays(-20)), Now.AddDays(-20)))).EnsureSuccessStatusCode();
        await app.ApplyAsync();

        Alerts(owner).ShouldHaveSingleItem().Body.ShouldContain("paso 3 de 14");
        Alerts(stuckOwner).ShouldBeEmpty(); // ticked weeks ago: no news today

        var staff = await app.StaffAsync();
        var overview = await staff.Client.GetFromJsonAsync<JsonElement>("/api/platform/metrics/overview");
        var rows = overview.GetProperty("certification").EnumerateArray().ToList();
        var parado = rows.Single(r => r.GetProperty("storeId").GetGuid() == stuck.StoreId);
        (parado.GetProperty("currentStep").GetInt32(), parado.GetProperty("daysInStep").GetInt32(), parado.GetProperty("stuck").GetBoolean()).ShouldBe((1, 20, true));
        var avance = rows.Single(r => r.GetProperty("storeId").GetGuid() == store.StoreId);
        (avance.GetProperty("currentStep").GetInt32(), avance.GetProperty("daysInStep").GetInt32(), avance.GetProperty("stuck").GetBoolean()).ShouldBe((2, 2, false));
        rows.IndexOf(parado).ShouldBeLessThan(rows.IndexOf(avance));
        (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/platform/stores/{store.StoreId}")).GetProperty("certification").GetProperty("stepsDone").GetInt32().ShouldBe(2);

        // The last step ticked: certified, a stage of the funnel. An older event arriving late changes nothing.
        var before = overview.GetProperty("funnel").EnumerateArray().Single(f => f.GetProperty("stage").GetString() == FunnelStages.Certified).GetProperty("tenants").GetInt32();
        (await store.SendAsync(
            Events.Envelope(new CertificationProgressedV1([.. Enumerable.Range(0, CertificationSteps.Count)], CertificationSteps.Count, Now), Now.AddMinutes(1)),
            Events.Envelope(new CertificationProgressedV1([0], CertificationSteps.Count, Now.AddDays(-5)), Now.AddMinutes(-1)))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        overview = await staff.Client.GetFromJsonAsync<JsonElement>("/api/platform/metrics/overview");
        overview.GetProperty("funnel").EnumerateArray().Single(f => f.GetProperty("stage").GetString() == FunnelStages.Certified).GetProperty("tenants").GetInt32().ShouldBe(before + 1);
        (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/platform/tenants/{owner.TenantId}")).GetProperty("metrics").GetProperty("stage").GetString()
            .ShouldBe(FunnelStages.Certified);
        Alerts(owner).Count.ShouldBe(1); // one progress alert per day
    }

    [Fact]
    public async Task Received_e_CF_are_listed_in_the_panel_and_only_an_owner_may_approve()
    {
        var owner = await app.OwnerAsync();
        var member = await MemberAsync(owner);
        var other = await app.OwnerAsync();
        var id = await app.InDbAsync(async db =>
        {
            var doc = new FeReceivedDocument
            {
                TenantId = owner.TenantId, Environment = "ecf", IssuerRnc = "131245678", IssuerName = "Distribuidora del Cibao", BuyerRnc = "101000001",
                ENcf = "E310000000007", Type = 31, IssuedOn = CloudApp.Today, Total = 1180m, Itbis = 180m, ReceivedUtc = Now,
                XmlGzip = FeXmlTestZip("<ECF><Encabezado/></ECF>"),
            };
            db.FeReceived.Add(doc);
            await db.SaveChangesAsync();
            return doc.Id;
        });

        var list = await member.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe/received");
        var item = list.EnumerateArray().ShouldHaveSingleItem();
        (item.GetProperty("issuerName").GetString(), item.GetProperty("total").GetDecimal(), item.GetProperty("approval").ValueKind).ShouldBe(("Distribuidora del Cibao", 1180m, JsonValueKind.Null));
        item.TryGetProperty("xmlGzip", out _).ShouldBeFalse();
        (await member.Client.GetFromJsonAsync<JsonElement>($"/api/panel/fe/received/{id}/xml")).GetProperty("xml").GetString().ShouldBe("<ECF><Encabezado/></ECF>");
        (await other.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe/received")).GetArrayLength().ShouldBe(0);
        (await other.Client.GetAsync($"/api/panel/fe/received/{id}/xml")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await member.Client.PostAsJsonAsync($"/api/panel/fe/received/{id}/approval", new { approve = true })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // The owner may: without the certificate consent the cloud cannot sign the approval yet.
        (await (await owner.Client.PostAsJsonAsync($"/api/panel/fe/received/{id}/approval", new { approve = true })).ErrorCode()).ShouldBe("Fe.NoConsent");
        (await (await other.Client.PostAsJsonAsync($"/api/panel/fe/received/{id}/approval", new { approve = true })).ErrorCode()).ShouldBe("Fe.NotFound");
    }

    private static byte[] FeXmlTestZip(string xml)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(xml));
        return output.ToArray();
    }
}
