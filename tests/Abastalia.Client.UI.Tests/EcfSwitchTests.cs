using Bunit;
using Shouldly;
using Abastalia.Client.UI.Api;
using Abastalia.Client.UI.Components;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-221: moving to Abastalia when the business already issues e-CF with another program.</summary>
public sealed class EcfSwitchTests
{
    [Theory]
    [InlineData("E320000004817", "Consumer32", 4818L, "E320000004818")]
    [InlineData(" e310000000009 ", "FiscalCredit31", 10L, "E310000000010")]
    [InlineData("E340000000000", "CreditNote34", 1L, "E340000000001")]
    public void The_next_free_number_follows_the_last_one_the_other_program_used(string last, string type, long number, string encf) =>
        EcfSwitch.After(last).ShouldBe(new EcfSwitch.NextNumber(type, number, encf));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("B0200000001")]        // paper NCF
    [InlineData("E41000000001")]       // too short
    [InlineData("E410000000001")]      // a type Abastalia does not issue
    [InlineData("E32000000481X")]
    [InlineData("E32-000004817")]
    [InlineData("E329999999999")]      // no number after the last one
    public void Anything_but_an_e_NCF_Abastalia_issues_has_no_next_number(string? last) => EcfSwitch.After(last).ShouldBeNull();

    [Fact]
    public void The_checklist_ticks_only_what_the_store_can_see()
    {
        var empty = EcfSwitch.Checklist(null, null, [], "Simulated");
        empty.Select(i => i.Done).ShouldBe([false, false, null, false, null, false]);

        var expired = new EcfCertificateStatus(true, true, "Ana", "101000001", "Avansi", DateTime.UtcNow.AddDays(-1), "AB", "File", -1, true);
        EcfSwitch.Checklist(expired, null, [], "eCF")[0].Done.ShouldBe(false);

        var cert = expired with { NotAfterUtc = DateTime.UtcNow.AddYears(1), DaysLeft = 365, ExpiresSoon = false };
        var receiver = new EcfReceiverStatus(true, "101000001", "Ana", null, null, "Ana", "Ana", []);
        EcfSequence Range(string env, long available) => new(Guid.NewGuid(), "Consumer32", env, true, "E320000000001", "E320000000100", "E320000000001", available, 0, null);
        EcfSwitch.Checklist(cert, receiver, [Range("TesteCF", 100), Range("eCF", 0)], "TesteCF")[3].Done.ShouldBe(false);
        EcfSwitch.Checklist(cert, receiver, [Range("eCF", 100)], "eCF").Select(i => i.Done).ShouldBe([true, true, null, true, null, true]);
    }

    [Fact]
    public void The_rest_of_the_sequence_is_loaded_for_production_from_the_next_free_number()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/receiver", new
            {
                active = true, rnc = "101000001",
                urls = new[] { new { environment = "ecf", reception = "https://fe.abastalia.com/ecf/101000001/fe/recepcion/api/ecf", approval = "https://fe.abastalia.com/ecf/101000001/fe/aprobacioncomercial/api/ecf", authentication = "https://fe.abastalia.com/ecf/101000001/fe/autenticacion/api/semilla" } },
            })
            .Json("GET", "/api/fiscal/sequences", Array.Empty<object>())
            .Json("POST", "/api/fiscal/sequences", new { id = Guid.NewGuid() }, System.Net.HttpStatusCode.Created);
        var panel = ctx.Render<EcfSwitchPanel>(p => p.Add(x => x.Environment, "Simulated"));

        panel.WaitForAssertion(() => panel.Find("dl").TextContent.ShouldContain("https://fe.abastalia.com/ecf/101000001/fe/recepcion/api/ecf"));
        panel.Find("form").Submit();
        panel.Markup.ShouldContain("Escriba un e-NCF completo");
        api.Requests.ShouldNotContain(r => r.Method == "POST");

        panel.FindAll("form input")[0].Change("E320000004817");
        panel.FindAll("form input")[1].Change("5000");
        panel.Find("form [role=status]").TextContent.ShouldContain("E320000004818");
        panel.Find("form").Submit();

        panel.WaitForAssertion(() => panel.Find("p[role=status]").TextContent.ShouldContain("Abastalia emitirá desde E320000004818 en producción"));
        var body = api.Requests.Single(r => r.Method == "POST").Body;
        body.ShouldContain("\"type\":\"Consumer32\"");
        body.ShouldContain("\"from\":4818");
        body.ShouldContain("\"to\":5000");
        body.ShouldContain("\"environment\":\"eCF\"");
    }
}
