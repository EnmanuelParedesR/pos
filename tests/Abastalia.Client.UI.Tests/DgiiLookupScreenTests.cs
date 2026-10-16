using System.Net;
using Bunit;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;
using static Abastalia.Client.UI.Tests.AdminInventoryTests;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-252: the DGII status next to a typed RNC, and a supplier NCF the DGII refuses saved only with a reason.</summary>
public sealed class DgiiLookupScreenTests
{
    private static readonly Guid Farm = Guid.NewGuid();

    [Fact]
    public void A_typed_RNC_shows_its_DGII_status_as_a_badge()
    {
        var (ctx, api) = ScreenContext.Create("purchasing.manage");
        using var _ = ctx;
        api.Json("GET", "/api/purchasing/suppliers", Array.Empty<object>())
           .Json("GET", "/api/fiscal/taxpayers/131245678", new
           {
               rnc = "131245678", name = "COLMADO EL SOL SRL", tradeName = (string?)null, isActive = false, status = "SUSPENDIDO", category = (string?)null, fromDgii = true,
           })
           .Json("GET", "/api/fiscal/taxpayers/101000001", new
           {
               rnc = "101000001", name = "FERRETERÍA CENTRAL SRL", tradeName = (string?)null, isActive = true, status = "ACTIVO", category = (string?)null, fromDgii = true,
           });
        var page = ctx.Render<Pages.Admin.Suppliers>();
        page.WaitForAssertion(() => Button(page, "Nuevo suplidor").Click());

        page.Find("[role=dialog] input[maxlength='13']").Change("131245678");
        page.WaitForAssertion(() => page.Find(".esc-pill--warning").TextContent.ShouldBe("Suspendido"));
        page.Markup.ShouldContain("La DGII lo muestra como suspendido.");

        page.Find("[role=dialog] input[maxlength='13']").Change("101000001");
        page.WaitForAssertion(() => page.Find(".esc-pill--success").TextContent.ShouldBe("Activo"));
        page.FindAll(".esc-pill--warning").ShouldBeEmpty();
    }

    [Fact]
    public void An_NCF_the_DGII_refuses_asks_for_a_reason_and_is_sent_again_with_it()
    {
        var (ctx, api) = ScreenContext.Create("payables.pay");
        using var _ = ctx;
        api.Json("GET", "/api/purchasing/expenses", Array.Empty<object>())
           .Json("GET", "/api/purchasing/suppliers", new[]
            {
                new { id = Farm, name = "Granja del Cibao", rnc = (string?)"101234567", phone = (string?)null, contact = (string?)null, creditDays = 0, isActive = true },
            })
           .Respond("POST", "/api/purchasing/expenses", body => body.Contains("\"ncfOverrideReason\":null", StringComparison.Ordinal)
                ? FakeApi.Problem(HttpStatusCode.BadRequest, "Purchasing.NcfNotValidAtDgii", "NCF no válido según la DGII: no corresponde a este suplidor o no existe.")
                : FakeApi.Response(HttpStatusCode.Created, new { id = Guid.NewGuid(), total = 118m }));
        var page = ctx.Render<Expenses>();
        page.WaitForAssertion(() => Button(page, "Nuevo gasto").Click());

        page.WaitForAssertion(() => page.Find("[role=dialog] select").Change(Farm.ToString()));
        page.Find("input[maxlength='13']").Change("B0199999999");
        page.Find("input[maxlength='150']").Change("Papelería");
        page.Find("input[inputmode=decimal]").Input("100");
        Button(page, "Registrar gasto").Click();

        page.WaitForAssertion(() => page.Find(".esc-field__error").TextContent.ShouldContain("NCF no válido según la DGII"));
        page.Find("input[maxlength='200']").Change("El suplidor confirmó el NCF");
        Button(page, "Registrar gasto").Click();

        page.WaitForAssertion(() => api.Requests.Count(r => r.Method == "POST").ShouldBe(2));
        api.Requests.Last(r => r.Method == "POST").Body.ShouldContain("\"ncfOverrideReason\":\"El suplidor confirm"); // the JSON escapes the accent
    }
}
