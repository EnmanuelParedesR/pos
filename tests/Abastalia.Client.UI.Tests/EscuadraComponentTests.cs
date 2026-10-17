using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages;
using Abastalia.Client.UI.State;
using Abastalia.Escuadra.Components;
using Abastalia.Hardware;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-243: each Escuadra piece on its own (docs/sistema-de-diseno.md, "bUnit por componente"): what it renders, what it tells a
/// screen reader, and how it answers the keyboard. The pieces that live in the register's screens (status bar, key bar, scanner, cart line,
/// scale readout on the Sale screen) are checked on those screens.</summary>
public sealed class EscuadraComponentTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public EscuadraComponentTests() => _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Button_is_a_plain_button_with_its_variant_and_its_visible_key()
    {
        var clicks = 0;
        var b = _ctx.Render<Button>(p => p.Add(x => x.Variant, Button.Variants.Primary).Add(x => x.Money, true).Add(x => x.Shortcut, "F12")
            .Add(x => x.OnClick, () => clicks++).AddChildContent("Cobrar"));

        var el = b.Find("button");
        el.GetAttribute("type").ShouldBe("button"); // never submits a form by accident
        el.ClassList.ShouldBe(["esc-button", "esc-button--primary", "esc-button--money"], ignoreOrder: true);
        b.Find("kbd.esc-keycap").TextContent.ShouldBe("F12");
        el.Click();
        clicks.ShouldBe(1);

        _ctx.Render<Button>(p => p.Add(x => x.Disabled, true).Add(x => x.Variant, Button.Variants.Danger)).Find("button").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void Keycap_is_a_kbd() => _ctx.Render<Keycap>(p => p.AddChildContent("Esc")).Markup.ShouldBe("<kbd class=\"esc-keycap\">Esc</kbd>");

    [Fact]
    public void Keypad_is_labelled_keeps_the_focus_in_the_field_and_sends_each_key()
    {
        var keys = new List<string>();
        var k = _ctx.Render<Keypad>(p => p.Add(x => x.OnKey, (string key) => keys.Add(key)));

        k.Find("[role=group]").GetAttribute("aria-label").ShouldBe("Teclado numérico");
        var buttons = k.FindAll("button");
        buttons.Count.ShouldBe(12);
        buttons.ShouldAllBe(b => b.GetAttribute("onmousedown") == null && b.GetAttribute("type") == "button"); // TK-324: no inline script (the Lite CSP)
        k.Find("[aria-label=Borrar]").Click();
        k.Find("[aria-label=Limpiar]").Click();
        k.FindAll("button")[0].Click();
        keys.ShouldBe(["backspace", "clear", "7"]);

        _ctx.Render<Keypad>(p => p.Add(x => x.WithDot, true)).FindAll("button").Select(b => b.TextContent).ShouldContain(".");
        (Keypad.Apply("12", "backspace"), Keypad.Apply("1.5", "."), Keypad.Apply("12", "clear"), Keypad.Apply("12345", "6", max: 5)).ShouldBe(("1", "1.5", "", "12345"));
    }

    [Fact]
    public void PinDots_says_how_many_digits_without_showing_them()
    {
        var pin = _ctx.Render<PinDots>(p => p.Add(x => x.Length, 2));
        pin.Find("[role=img]").GetAttribute("aria-label").ShouldBe("2 de 4 dígitos");
        pin.FindAll("span.filled").Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(Notice.Kinds.Error, "alert")]
    [InlineData(Notice.Kinds.Warning, "alert")]
    [InlineData(Notice.Kinds.Success, "status")]
    [InlineData(Notice.Kinds.Info, "status")]
    public void Notice_is_inline_and_announced_by_how_urgent_it_is(Notice.Kinds kind, string role)
    {
        var n = _ctx.Render<Notice>(p => p.Add(x => x.Kind, kind).Add(x => x.Title, "No se encontró el código."));
        var el = n.Find(".esc-notice");
        el.GetAttribute("role").ShouldBe(role);
        el.ClassList.ShouldContain($"esc-notice--{kind.ToString().ToLowerInvariant()}");
        n.Find("svg").GetAttribute("aria-hidden").ShouldBe("true"); // the words carry the meaning, not the icon
    }

    [Fact]
    public void Dialog_is_modal_named_by_its_title_and_Esc_closes_it()
    {
        var (closed, keys) = (0, new List<string>());
        var d = _ctx.Render<Dialog>(p => p.Add(x => x.Title, "¿Anular esta venta?").Add(x => x.OnClose, () => closed++)
            .Add(x => x.OnKey, (KeyboardEventArgs e) => keys.Add(e.Key)));

        var dialog = d.Find("[role=dialog]");
        dialog.GetAttribute("aria-modal").ShouldBe("true");
        d.Find($"#{dialog.GetAttribute("aria-labelledby")}").TextContent.ShouldBe("¿Anular esta venta?");
        d.Find(".esc-overlay").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        d.Find(".esc-overlay").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        (closed, keys.Single()).ShouldBe((1, "Enter"));
    }

    [Fact]
    public void Segmented_marks_the_chosen_option_and_reports_a_new_one()
    {
        string? chosen = null;
        var s = _ctx.Render<Segmented>(p => p.Add(x => x.Label, "Periodo").Add(x => x.Value, "today")
            .Add(x => x.Options, [("today", "Hoy"), ("week", "7 días")]).Add(x => x.ValueChanged, (string v) => chosen = v));

        s.Find("[role=group]").GetAttribute("aria-label").ShouldBe("Periodo");
        s.FindAll("button").Select(b => b.GetAttribute("aria-pressed")).ShouldBe(["true", "false"]);
        s.FindAll("button")[1].Click();
        chosen.ShouldBe("week");
    }

    [Fact]
    public void Field_shows_its_hint_or_its_error_as_an_alert()
    {
        _ctx.Render<Field>(p => p.Add(x => x.Label, "RNC").Add(x => x.Hint, "9 dígitos")).Find(".esc-field__hint").TextContent.ShouldBe("9 dígitos");
        var f = _ctx.Render<Field>(p => p.Add(x => x.Label, "RNC").Add(x => x.Hint, "9 dígitos").Add(x => x.Error, "El RNC tiene 9 dígitos."));
        f.Find("[role=alert]").TextContent.ShouldBe("El RNC tiene 9 dígitos.");
        f.FindAll(".esc-field__hint").ShouldBeEmpty();
    }

    [Fact]
    public void Money_and_Delta_say_it_in_words_and_signs_not_color_alone()
    {
        _ctx.Render<Money>(p => p.Add(x => x.Value, 1250.5m)).Find(".esc-money__currency").TextContent.ShouldBe("RD$");
        _ctx.Render<Delta>(p => p.Add(x => x.Current, 112m).Add(x => x.Previous, 100m)).Find(".esc-delta--up").TextContent.Trim().ShouldBe("+12 %");
        _ctx.Render<Delta>(p => p.Add(x => x.Current, 88m).Add(x => x.Previous, 100m)).Find(".esc-delta--down").TextContent.Trim().ShouldBe("−12 %");
        _ctx.Render<Delta>(p => p.Add(x => x.Current, 100m).Add(x => x.Previous, 100m)).Find(".esc-delta--flat").TextContent.Trim().ShouldBe("Igual");
    }

    [Fact]
    public void BarChart_reads_each_column_aloud_and_is_reachable_by_keyboard_or_says_it_is_empty()
    {
        var chart = _ctx.Render<BarChart>(p => p.Add(x => x.Label, "Ventas").Add(x => x.Points, [new BarChart.Point("L", "Lunes", 100m, 80m)]));
        var col = chart.Find("[role=listitem]");
        col.GetAttribute("tabindex").ShouldBe("0");
        col.GetAttribute("aria-label")!.ShouldStartWith("Lunes: ");

        _ctx.Render<BarChart>(p => p.Add(x => x.Label, "Ventas").Add(x => x.Points, [new BarChart.Point("L", "Lunes", 0m)]))
            .Find(".esc-empty[role=status]").TextContent.ShouldContain("Todavía no hay ventas");
    }

    [Fact]
    public void Decorative_pieces_are_hidden_from_screen_readers()
    {
        _ctx.Render<Icon>(p => p.Add(x => x.Name, "scale")).Find("svg").GetAttribute("aria-hidden").ShouldBe("true");
        _ctx.Render<Skeleton>().Find("span").GetAttribute("aria-hidden").ShouldBe("true");
        _ctx.Render<Sparkline>(p => p.Add(x => x.Values, [1m, 2m])).Find("svg").GetAttribute("aria-hidden").ShouldBe("true");
        _ctx.Render<Logo>().Find("svg").GetAttribute("aria-label").ShouldBe("Abastalia");
    }

    [Theory]
    [InlineData(ScaleReadout.States.Stable, 1.75, "1.750", "Estable", "success")]
    [InlineData(ScaleReadout.States.Weighing, 0.4, "0.400", "Pesando", "warning")]
    [InlineData(ScaleReadout.States.Empty, 0, "0.000", "Vacía", "neutral")]
    [InlineData(ScaleReadout.States.Overload, 99, "99.000", "Sobrecarga", "error")]
    [InlineData(ScaleReadout.States.Disconnected, 0, "-.---", "Desconectada", "warning")]
    public void ScaleReadout_shows_three_decimals_and_its_state_in_words(ScaleReadout.States state, double pounds, string digits, string word, string tone)
    {
        var r = _ctx.Render<ScaleReadout>(p => p.Add(x => x.State, state).Add(x => x.Pounds, (decimal)pounds));

        r.Find("[role=group]").GetAttribute("aria-label").ShouldBe("Balanza");
        r.Find(".esc-scale__value").TextContent.ShouldBe($"{digits} lb");
        var pill = r.Find(".esc-pill");
        (pill.TextContent, pill.GetAttribute("role")).ShouldBe((word, "status"));
        pill.ClassList.ShouldContain($"esc-pill--{tone}");
        r.FindAll("[aria-live]").ShouldBeEmpty(); // four readings a second are not read aloud
    }

    // ---- On the register's screens ----

    private static async Task<(BunitContext Ctx, FakeApi Api, TestDrivers Hw)> RegisterAsync(HardwareSettings settings)
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        await ctx.Services.GetRequiredService<Peripherals>().SaveAsync(settings);
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>());
        return (ctx, api, ctx.Services.GetRequiredService<TestDrivers>());
    }

    [Fact]
    public async Task The_Sale_screen_shows_the_scale_fixed_while_this_PC_has_one()
    {
        var (ctx, _, hw) = await RegisterAsync(new(Scale: new("COM3", ScaleProtocol.Cas)));
        using var _ = ctx;
        hw.Scale.Reading = new(1.255m, ScaleStatus.Stable);
        var sale = ctx.Render<Sale>();

        sale.WaitForAssertion(() => sale.Find(".sale__totals .esc-scale .esc-pill").TextContent.ShouldBe("Estable"));
        sale.Find(".esc-scale__value").TextContent.ShouldBe("1.255 lb");
        hw.Scale.Reading = ScaleReading.Disconnected;
        ctx.Services.GetRequiredService<FakeTimeProvider>().Advance(TimeSpan.FromMilliseconds(500)); // the screen's next reading
        sale.WaitForAssertion(() => sale.Find(".esc-scale .esc-pill").TextContent.ShouldBe("Desconectada"));

        var (plain, _, _) = await RegisterAsync(new());
        using var __ = plain;
        plain.Render<Sale>().FindAll(".esc-scale").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Sale_screen_names_its_scanner_its_lines_and_shows_each_key()
    {
        var (ctx, _, _) = await RegisterAsync(new());
        using var _ = ctx;
        var sale = ctx.Render<Sale>();

        sale.Find("input.sale__scanner").GetAttribute("aria-label").ShouldBe("Código de barras o PLU, o nombre del producto");
        sale.Find("ol.sale__lines").GetAttribute("aria-label").ShouldBe("Líneas");
        var keys = sale.Find("nav.esc-key-bar");
        keys.GetAttribute("aria-label").ShouldBe("Teclas");
        keys.QuerySelectorAll("button").ShouldAllBe(b => b.QuerySelector("kbd.esc-keycap") != null); // the screen teaches (principle 5)
    }

    [Fact]
    public async Task The_status_bar_shows_the_scale_and_the_printer_configured_connected_or_failing()
    {
        var (ctx, _, hw) = await RegisterAsync(new(new PrinterSettings("EPSON TM-T20"), new ScaleSettings("COM3", ScaleProtocol.Cas)));
        using var _ = ctx;
        var frame = ctx.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "venta"))));
        string[] Devices() => [.. frame.FindAll(".esc-status-bar__device").Select(d => d.TextContent.Trim() + (d.ClassList.Contains("esc-offline") ? " (ámbar)" : ""))];

        frame.WaitForAssertion(() => Devices().ShouldBe(["Balanza configurada", "Impresora configurada"]));

        var peripherals = ctx.Services.GetRequiredService<Peripherals>();
        await peripherals.ReadScaleAsync(CancellationToken.None);
        hw.Printer.Offline = true;
        await peripherals.TryAsync(p => p.OpenDrawerAsync(CancellationToken.None));
        frame.WaitForAssertion(() => Devices().ShouldBe(["Balanza conectada", "Impresora sin respuesta (ámbar)"]));

        hw.Scale.Reading = ScaleReading.Disconnected;
        hw.Printer.Offline = false;
        await peripherals.ReadScaleAsync(CancellationToken.None);
        await peripherals.TryAsync(p => p.OpenDrawerAsync(CancellationToken.None));
        frame.WaitForAssertion(() => Devices().ShouldBe(["Balanza sin respuesta (ámbar)", "Impresora conectada"]));
    }
}
