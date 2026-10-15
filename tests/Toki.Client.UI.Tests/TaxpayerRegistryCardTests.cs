using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Shouldly;
using Toki.Client.UI.Components;

namespace Toki.Client.UI.Tests;

/// <summary>TK-162: the owner uploads the DGII registry and watches the background import without the screen blocking.</summary>
public sealed class TaxpayerRegistryCardTests
{
    private static object Status(bool running, int percent, int taxpayers) =>
        new { running, percent, taxpayers, error = (string?)null, finishedUtc = running ? (DateTime?)null : DateTime.UtcNow };

    [Fact]
    public void The_file_is_uploaded_as_is_and_the_progress_is_followed_until_it_ends()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/taxpayer-registry/import", new { running = false, percent = 0, taxpayers = 0 })
           .Respond("POST", "/api/fiscal/taxpayer-registry", _ =>
           {
               api.Json("GET", "/api/fiscal/taxpayer-registry/import", Status(false, 100, 712_345));
               return FakeApi.Response(HttpStatusCode.Accepted, Status(true, 0, 0));
           });
        var card = ctx.Render<TaxpayerRegistryCard>();

        card.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("131245678|COLMADO EL SOL SRL", "DGII_RNC.TXT"));

        card.WaitForAssertion(() => card.Markup.ShouldContain("Padrón cargado: 712,345 contribuyentes"), TimeSpan.FromSeconds(5));
        api.Requests.Single(r => r.Method == "POST").Body.ShouldBe("131245678|COLMADO EL SOL SRL");
    }

    [Fact]
    public void An_import_already_running_shows_its_progress_and_the_file_picker_waits()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/taxpayer-registry/import", Status(true, 42, 300_000));
        var card = ctx.Render<TaxpayerRegistryCard>();

        card.WaitForAssertion(() => card.Markup.ShouldContain("Cargando el padrón: 42 % (300,000 contribuyentes)"));
        card.Find("input[type=file]").HasAttribute("disabled").ShouldBeTrue();
    }
}
