using Bunit;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;

namespace Abastalia.Client.UI.Tests;

/// <summary>R9 #1 and #5: "Comprobantes recibidos" offers "Registrar compra" only for a production e-CF that arrived once.</summary>
public sealed class ReceivedEcfTests
{
    private static object Received(string environment, string encf, string item = "Res") => new
    {
        id = Guid.NewGuid(), environment, issuerRnc = "101672919", issuerName = $"Distribuidora ({item})", eNcf = encf, type = 31, issuedOn = new DateOnly(2026, 10, 15),
        total = 1_180m, itbis = 180m, receivedUtc = DateTime.UtcNow, approval = (string?)null, approvalReason = (string?)null, approvalUtc = (DateTime?)null,
        approvalDeliveredUtc = (DateTime?)null, approvalDgiiUtc = (DateTime?)null, approvalMessage = (string?)null,
    };

    [Fact]
    public void Only_a_production_e_CF_that_arrived_once_can_be_registered_as_a_purchase()
    {
        var (ctx, api) = ScreenContext.Create();
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/received", new[]
        {
            Received("ecf", "E310000000001"), Received("testecf", "E310000000002"),
            Received("ecf", "E310000000003", "Res"), Received("ecf", "E310000000003", "Pollo"),
        });
        var page = ctx.Render<EcfReceived>();

        page.WaitForAssertion(() => page.FindAll("tbody tr").Count.ShouldBe(4));
        var rows = page.FindAll("tbody tr");
        rows[0].TextContent.ShouldContain("Registrar compra");
        rows[1].TextContent.ShouldNotContain("Registrar compra"); // TesteCF: a test, not a purchase
        rows[2].TextContent.ShouldContain("Llegó dos veces distinto");
        rows[2].TextContent.ShouldNotContain("Registrar compra");
        rows[3].TextContent.ShouldContain("Llegó dos veces distinto");
    }
}
