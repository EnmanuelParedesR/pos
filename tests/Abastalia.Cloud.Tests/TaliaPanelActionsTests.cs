using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Fe;
using Abastalia.Host.Cloud.Persistence;

namespace Abastalia.Cloud.Tests;

public sealed class MailingActionsApp() : CloudApp(new ProposeModel())
{
    public ProposeModel Model => (ProposeModel)Services.GetService(typeof(Abastalia.Host.Cloud.Assistant.IChatModel))!;
    public RecordingEmailSender Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(Mail));
    }
}

/// <summary>TK-341: the panel's writes through Talia run the endpoints' own code, with a summary from the database, and never on oneself.</summary>
public sealed class TaliaPanelActionsTests(ActionsApp app) : IClassFixture<ActionsApp>
{
    private static async Task<JsonElement> AskAsync(Owner who, string question) =>
        await (await who.Client.PostAsJsonAsync("/api/assistant/ask", new { question })).EnsureSuccessStatusCode().Json();

    /// <summary>Proposes and confirms; returns the card's summary and the confirmed response.</summary>
    private static async Task<(string Summary, JsonElement Done)> DoAsync(Owner who, string question)
    {
        var proposal = (await AskAsync(who, question)).GetProperty("proposal");
        proposal.ValueKind.ShouldBe(JsonValueKind.Object, question);
        var done = await (await who.Client.PostAsync($"/api/assistant/actions/{proposal.GetProperty("id").GetGuid()}/confirm", null)).Json();
        done.GetProperty("status").GetString().ShouldBe("Done", done.GetRawText());
        return (proposal.GetProperty("summary").GetString()!, done);
    }

    private async Task<string> RefusedAsync(Owner who, string question)
    {
        (await AskAsync(who, question)).GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null, question);
        return app.Model.ToolResults.Single();
    }

    private async Task<Owner> MemberAsync(Owner owner, string role = "ReadOnly")
    {
        var email = $"member{Guid.NewGuid():N}@example.com";
        var link = (await (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role })).Json()).GetProperty("link").GetString()!;
        (await app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = link[(link.IndexOf('#') + 1)..], password = CloudApp.Password })).EnsureSuccessStatusCode();
        return await app.SignInAsync(email);
    }

    private static async Task<JsonElement> TeamMemberAsync(Owner owner, string email) =>
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/members")).GetProperty("members").EnumerateArray().Single(m => m.GetProperty("email").GetString() == email);

    [Fact]
    public async Task An_owner_changes_another_members_role_and_costs_but_never_their_own()
    {
        var owner = await app.OwnerAsync();
        var member = await MemberAsync(owner);

        var (summary, _) = await DoAsync(owner, $$"""propose_member_costs {"email":"{{member.Email.ToUpperInvariant()}}","canViewCosts":true}""");
        summary.ShouldBe($"«Ver costos» de {member.Email} (Solo lectura): no → sí");
        (await TeamMemberAsync(owner, member.Email)).GetProperty("canViewCosts").GetBoolean().ShouldBeTrue();

        (summary, _) = await DoAsync(owner, $$"""propose_member_role {"email":"{{member.Email}}","role":"Owner"}""");
        summary.ShouldBe($"Rol de {member.Email} en el panel: Solo lectura → Dueño");
        app.Model.ToolResults.Single().ShouldNotContain("userId"); // the model hears the card, not the resolved ids
        (await TeamMemberAsync(owner, member.Email)).GetProperty("role").GetString().ShouldBe("Owner");

        // E8: not one's own role or permissions (the other owner could, from Equipo).
        (await RefusedAsync(owner, $$"""propose_member_role {"email":"{{owner.Email}}","role":"ReadOnly"}""")).ShouldContain("Su propio rol");
        (await RefusedAsync(member, $$"""propose_member_costs {"email":"{{member.Email}}","canViewCosts":false}""")).ShouldContain("Su propio rol");
        (await RefusedAsync(owner, """propose_member_role {"email":"nadie@example.com","role":"Owner"}""")).ShouldContain("No hay nadie con ese correo");
    }

    [Fact]
    public async Task A_read_only_member_is_offered_none_of_them()
    {
        var owner = await app.OwnerAsync();
        var member = await MemberAsync(owner);

        await AskAsync(member, "Hola");

        app.Model.Offered.Where(t => t.Name.StartsWith("propose_", StringComparison.Ordinal)).Select(t => t.Name).ShouldBe(["propose_alert_preference"]);
        (await RefusedAsync(member, """propose_create_store {"name":"Otra"}""")).ShouldContain("No existe la herramienta");
    }

    [Fact]
    public async Task Creating_a_store_and_its_connection_code_shows_the_code_only_on_the_card()
    {
        var owner = await app.OwnerAsync();

        var (summary, done) = await DoAsync(owner, """propose_create_store {"name":"  Sucursal Norte "}""");
        summary.ShouldBe("Crear la tienda «Sucursal Norte»");
        done.GetProperty("result").GetString()!.ShouldContain("Sucursal Norte");
        (await DoAsync(owner, """propose_create_store {"name":"sucursal norte"}""")).Summary.ShouldEndWith("(ya hay una tienda con ese nombre)");

        (await RefusedAsync(owner, """propose_connection_code {"store":"norte"}""")).ShouldContain("Hay 2 tiendas");
        (await RefusedAsync(owner, """propose_connection_code {"store":"Sur"}""")).ShouldContain("No encontr");
        await DoAsync(owner, """propose_create_store {"name":"Sur"}""");
        (summary, done) = await DoAsync(owner, """propose_connection_code {"store":"sur"}""");

        summary.ShouldBe("Código de conexión para la tienda «Sur» (vale 30 minutos y sirve una sola vez)");
        var code = await app.InDbAsync(db => db.ConnectionCodes.Where(c => c.TenantId == owner.TenantId).Select(c => c.Code).SingleAsync());
        done.GetProperty("result").GetString()!.ShouldStartWith($"Código: {code}.");
        app.Model.ToolResults.Single().ShouldNotContain(code);
    }

    [Fact]
    public async Task Cancelling_an_invite_and_inviting_without_e_mail_is_left_to_the_team_page()
    {
        var owner = await app.OwnerAsync();
        var email = $"pedro{Guid.NewGuid():N}@example.com";
        (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" })).EnsureSuccessStatusCode();

        var (summary, _) = await DoAsync(owner, $$"""propose_cancel_invite {"email":"{{email}}"}""");

        summary.ShouldBe($"Cancelar la invitación de {email} (Solo lectura, enviada el 15/10/2026)");
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/members")).GetProperty("invites").GetArrayLength().ShouldBe(0);
        (await RefusedAsync(owner, $$"""propose_cancel_invite {"email":"{{email}}"}""")).ShouldContain("No hay una invitaci");
        // Without an e-mail provider the link would be shown, and a proposal's result is stored: the team page does it.
        (await RefusedAsync(owner, """propose_invite_member {"email":"ana@example.com","role":"ReadOnly"}""")).ShouldContain("/app/members");
    }

    [Fact]
    public async Task Inviting_goes_by_e_mail_and_the_link_never_reaches_the_card()
    {
        await using var mailing = new MailingActionsApp();
        var owner = await mailing.OwnerAsync();
        var email = $"contador{Guid.NewGuid():N}@example.com";

        var proposal = (await AskAsync(owner, $$"""propose_invite_member {"email":"{{email}}","role":"ReadOnly"}""")).GetProperty("proposal");
        proposal.GetProperty("summary").GetString().ShouldBe($"Invitar a {email} al panel como Solo lectura (le llega un enlace por correo que vale 7 días)");
        var done = await (await owner.Client.PostAsync($"/api/assistant/actions/{proposal.GetProperty("id").GetGuid()}/confirm", null)).Json();

        done.GetProperty("result").GetString().ShouldBe($"Invitación enviada a {email}.");
        mailing.Mail.Sent.ShouldContain(m => m.To == email && m.Body.Contains("/invite#"));
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/members")).GetProperty("invites").EnumerateArray().Single().GetProperty("email").GetString().ShouldBe(email);
    }

    [Fact]
    public async Task A_received_e_CF_is_accepted_or_rejected_with_its_reason_once()
    {
        var owner = await app.OwnerAsync();
        var rnc = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (a, b) = await app.InDbAsync(async db =>
        {
            db.FeCertificates.Add(new FeCertificate { Id = Guid.NewGuid(), TenantId = owner.TenantId, Rnc = rnc, KeyName = "x", ConsentVersion = FeConsent.Version });
            FeReceivedDocument Doc(string encf, string issuer) => new()
            {
                TenantId = owner.TenantId, Environment = "ecf", IssuerRnc = "131245678", IssuerName = issuer, BuyerRnc = rnc, ENcf = encf, Type = 31,
                IssuedOn = CloudApp.Today, Total = 1180m, Itbis = 180m, ReceivedUtc = app.Clock.GetUtcNow().UtcDateTime,
            };
            var docs = new[] { Doc("E310000000045", "Distribuidora del Cibao"), Doc("E310000000046", "Distribuidora del Cibao") };
            db.FeReceived.AddRange(docs);
            await db.SaveChangesAsync();
            return (docs[0].Id, docs[1].Id);
        });

        (await RefusedAsync(owner, """propose_received_decision {"document":"E310000000045","approve":false}""")).ShouldContain("motivo");
        (await RefusedAsync(owner, """propose_received_decision {"document":"cibao","approve":true}""")).ShouldContain("el e-NCF");

        var (summary, _) = await DoAsync(owner, """propose_received_decision {"document":"e310000000045","approve":false,"reason":"Precios no acordados"}""");

        summary.ShouldBe($"Rechazar el e-CF E310000000045 de Distribuidora del Cibao (RD$ 1,180.00, {CloudApp.Today:dd/MM/yyyy}). Motivo: «Precios no acordados»");
        var row = await app.InDbAsync(db => db.FeReceived.AsNoTracking().SingleAsync(d => d.Id == a));
        (row.PanelDecision, row.PanelReason, row.PanelDecidedBy).ShouldBe(("Rejected", "Precios no acordados", owner.Email));
        // Decided: no longer a candidate, so the supplier's name now picks the other one.
        (await DoAsync(owner, """propose_received_decision {"document":"Cibao","approve":true}""")).Summary.ShouldStartWith("Aceptar el e-CF E310000000046");
        (await app.InDbAsync(db => db.FeReceived.AsNoTracking().SingleAsync(d => d.Id == b))).PanelDecision.ShouldBe("Approved");
    }
}
