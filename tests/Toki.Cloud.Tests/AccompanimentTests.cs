using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Toki.Host.Cloud.Certification;

namespace Toki.Cloud.Tests;

/// <summary>TK-233 to TK-235: the store asks for accompaniment in the e-CF certification, staff schedule it in the console and the owner gets the
/// confirmation with the preparation list.</summary>
public sealed class AccompanimentTests(MailingCloudApp app) : IClassFixture<MailingCloudApp>
{
    private List<(string To, string Subject, string Body)> MailTo(string to)
    {
        lock (app.Mail.Sent) return [.. app.Mail.Sent.Where(m => m.To == to)];
    }

    [Fact]
    public async Task The_store_asks_once_staff_are_e_mailed_and_a_second_tap_sends_nothing_again()
    {
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner, "Colmado Acompañado");

        (await (await store.Client.PostAsync("/fiscal/v1/accompaniment/status", null)).Json()).GetProperty("request").ValueKind.ShouldBe(JsonValueKind.Null);
        (await (await store.Client.PostAsJsonAsync("/fiscal/v1/accompaniment", new { contactName = "", phone = "8095551234" })).ErrorCode())
            .ShouldBe("Accompaniment.Invalid");

        var sent = MailTo(CloudApp.SupportEmail).Count;
        var asked = await store.Client.PostAsJsonAsync("/fiscal/v1/accompaniment", new { contactName = "Ana Pérez", phone = "809-555-1234", preferredTimes = "Mañanas" });
        asked.StatusCode.ShouldBe(HttpStatusCode.OK);
        var request = (await asked.Json()).GetProperty("request");
        (request.GetProperty("status").GetString(), request.GetProperty("contactName").GetString()).ShouldBe(("New", "Ana Pérez"));

        var mail = MailTo(CloudApp.SupportEmail).Skip(sent).ShouldHaveSingleItem();
        mail.Subject.ShouldContain("Colmado Acompañado");
        mail.Body.ShouldContain("809-555-1234");
        mail.Body.ShouldContain(owner.Email);
        mail.Body.ShouldContain("Mañanas");

        // Pressed again while open: the same request back, no new e-mail.
        var again = await (await store.Client.PostAsJsonAsync("/fiscal/v1/accompaniment", new { contactName = "Otra", phone = "8095550000" })).Json();
        again.GetProperty("request").GetProperty("id").GetGuid().ShouldBe(request.GetProperty("id").GetGuid());
        MailTo(CloudApp.SupportEmail).Count.ShouldBe(sent + 1);
        (await app.InDbAsync(db => db.AccompanimentRequests.CountAsync(r => r.TenantId == owner.TenantId))).ShouldBe(1);

        // A panel user is not a node, and a node never reaches the console.
        (await owner.Client.PostAsync("/fiscal/v1/accompaniment/status", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await store.Client.GetAsync("/api/platform/accompaniment")).StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Staff_schedule_both_sessions_audited_and_the_owner_gets_each_confirmation_with_the_list_on_the_first()
    {
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner, "Carnicería Agenda");
        var id = (await (await store.Client.PostAsJsonAsync("/fiscal/v1/accompaniment", new { contactName = "Luis", phone = "8095557777" })).Json())
            .GetProperty("request").GetProperty("id").GetGuid();

        (await owner.Client.GetAsync("/api/platform/accompaniment")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var staff = await app.StaffAsync();
        var row = (await staff.Client.GetFromJsonAsync<JsonElement>("/api/platform/accompaniment")).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);
        (row.GetProperty("storeName").GetString(), row.GetProperty("tenantName").GetString(), row.GetProperty("status").GetString())
            .ShouldBe(("Carnicería Agenda", owner.Email, "New"));

        var session1 = new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc); // 10:00 AM at the store
        var audited = await app.InDbAsync(db => db.PlatformAudit.CountAsync());
        var updated = await staff.Client.PutAsJsonAsync($"/api/platform/accompaniment/{id}", new { status = "Scheduled", session1Utc = session1, notes = "Llamar antes" });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await updated.Json()).GetProperty("notes").GetString().ShouldBe("Llamar antes");
        (await app.InDbAsync(db => db.PlatformAudit.OrderBy(a => a.Id).Skip(audited).ToListAsync()))
            .ShouldContain(a => a.Method == "PUT" && a.Path == $"/api/platform/accompaniment/{id}" && a.Email == CloudApp.StaffEmail);

        var first = MailTo(owner.Email).Where(m => m.Subject.Contains("cita 1")).ShouldHaveSingleItem();
        first.Body.ShouldContain("20/10/2026 a las 10:00 AM");
        first.Body.ShouldContain("Toki nunca le pedirá esa clave");
        foreach (var item in AccompanimentChecklist.Items) first.Body.ShouldContain(item);

        // The store sees the status and date, never the staff notes.
        var mine = (await (await store.Client.PostAsync("/fiscal/v1/accompaniment/status", null)).Json()).GetProperty("request");
        mine.GetProperty("status").GetString().ShouldBe("Scheduled");
        mine.GetProperty("session1Utc").GetDateTime().ShouldBe(session1);
        mine.TryGetProperty("notes", out _).ShouldBeFalse();

        // Saving again with the same date sends nothing; session 2 gets its own e-mail without the list.
        var session2 = session1.AddDays(10);
        (await staff.Client.PutAsJsonAsync($"/api/platform/accompaniment/{id}", new { status = "Session1Done", session1Utc = session1, session2Utc = session2 }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        MailTo(owner.Email).Count(m => m.Subject.Contains("cita 1")).ShouldBe(1);
        var second = MailTo(owner.Email).Where(m => m.Subject.Contains("cita 2")).ShouldHaveSingleItem();
        second.Body.ShouldContain("declaración jurada");
        second.Body.ShouldNotContain(AccompanimentChecklist.Items[0]);

        // Closed: the store may ask again; a bad status or an unknown id is refused.
        (await staff.Client.PutAsJsonAsync($"/api/platform/accompaniment/{id}", new { status = "Cancelled" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.Client.PutAsJsonAsync($"/api/platform/accompaniment/{id}", new { status = (string?)null })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await staff.Client.PutAsJsonAsync($"/api/platform/accompaniment/{Guid.NewGuid()}", new { status = "New" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var renewed = await (await store.Client.PostAsJsonAsync("/fiscal/v1/accompaniment", new { contactName = "Luis", phone = "8095557777" })).Json();
        renewed.GetProperty("request").GetProperty("id").GetGuid().ShouldNotBe(id);
    }
}
