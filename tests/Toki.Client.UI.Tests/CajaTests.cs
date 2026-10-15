using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Client.UI.Api;
using Toki.Client.UI.Componentes;
using Toki.Client.UI.Estado;
using Toki.Client.UI.Paginas;

namespace Toki.Client.UI.Tests;

public sealed class CajaTests
{
    private static readonly ProductoPorCodigo Salami = new(Guid.NewGuid(), null, "Salami 1 lb", "Unidad", "General18", 210m, null, 1m, null, null, [], 24m);

    private static Comprobante ComprobanteDe(decimal total) => new("Carnicería", null, null, null, "Factura de Consumo Electrónica", "E320000000001", 1,
        DateTime.UtcNow, "Caja 1", "Yudelka M.", null, null, [], total, 0, [], total, [], 0, "abc123", "", null, false);

    [Fact]
    public void F7_abre_el_descuento_de_la_linea_no_la_cantidad()
    {
        var (ctx, api) = Contexto.Crear("ventas.registrar", "caja.operar");
        using var _ = ctx;
        api.Json("GET", "/api/inventario/categorias", Array.Empty<object>())
           .Json("GET", $"/api/inventario/productos/por-codigo/7460000000017", Salami);
        var venta = ctx.Render<Venta>();

        venta.Find("input.venta__escaner").Input("7460000000017");
        venta.Find("form").Submit();
        venta.WaitForAssertion(() => venta.Find(".venta__linea").TextContent.ShouldContain("Salami 1 lb"));

        venta.Find(".venta").KeyDown(new KeyboardEventArgs { Key = "F7" });
        venta.Find("[role=dialog] h2").TextContent.ShouldBe("Descuento");
        venta.Find("[role=dialog] input").Input("20");
        venta.Find("[role=dialog] .esc-boton--primario").Click();

        ctx.Services.GetRequiredService<Carrito>().Total.ShouldBe(168m);
    }

    [Fact]
    public void Si_el_servidor_corrige_el_total_se_muestra_y_se_confirma_otra_vez()
    {
        var (ctx, api) = Contexto.Crear("ventas.registrar");
        using var _ = ctx;
        var carrito = ctx.Services.GetRequiredService<Carrito>();
        carrito.Agregar(Salami, null);
        api.Responder("POST", "/api/ventas", _ => ApiFalsa.Problema(HttpStatusCode.Conflict, "Ventas.TotalCambio", "El total cambió.", new { total = new[] { "209.99" } }));
        VentaRegistrada? vendida = null;
        var cobro = ctx.Render<DialogoCobro>(p => p.Add(x => x.TurnoId, Contexto.Turno.TurnoId).Add(x => x.OnVendida, v => vendida = v));

        cobro.Find(".esc-boton--dinero").Click();
        cobro.WaitForAssertion(() => cobro.Find(".esc-aviso").TextContent.ShouldContain("RD$ 209.99"));
        vendida.ShouldBeNull();

        api.Json("POST", "/api/ventas", new VentaRegistrada(carrito.VentaId, 1, "E320000000001", 209.99m, 0m, ComprobanteDe(209.99m)), HttpStatusCode.Created);
        cobro.Find(".esc-boton--dinero").Click();
        cobro.WaitForAssertion(() => vendida.ShouldNotBeNull());
        // Same idempotency key both times, and the confirmed total is the server's.
        var envios = api.Pedidos.Where(p => p.Ruta == "/api/ventas").ToList();
        envios.Count.ShouldBe(2);
        envios[1].Cuerpo.ShouldContain("\"totalEsperado\":209.99");
        envios[1].Cuerpo.ShouldContain(carrito.VentaId.ToString());
    }

    [Fact]
    public void Un_descuento_mayor_pide_supervisor_y_reintenta_con_la_autorizacion()
    {
        var (ctx, api) = Contexto.Crear("ventas.registrar");
        using var _ = ctx;
        var carrito = ctx.Services.GetRequiredService<Carrito>();
        carrito.Agregar(Salami, null);
        var autorizacion = Guid.NewGuid();
        api.Responder("POST", "/api/ventas", cuerpo => cuerpo.Contains(autorizacion.ToString(), StringComparison.Ordinal)
                ? ApiFalsa.Respuesta(HttpStatusCode.Created, new VentaRegistrada(carrito.VentaId, 1, "E320000000001", 210m, 0m, ComprobanteDe(210m)))
                : ApiFalsa.Problema(HttpStatusCode.Forbidden, "Autorizacion.Requerida", "Necesita un supervisor (ventas.descuento-mayor)."))
           .Json("GET", "/api/identidad/usuarios/activos", new[] { new UsuarioActivo(Guid.NewGuid(), "Ramón D.", "ramon") })
           .Json("POST", "/api/identidad/autorizaciones", new AutorizacionDada(autorizacion, "Ramón D.", DateTime.UtcNow.AddMinutes(2)));
        VentaRegistrada? vendida = null;
        var pantalla = ctx.Render<Fragmento>(p => p.Add(x => x.Contenido, b =>
        {
            b.OpenComponent<DialogoCobro>(0);
            b.AddAttribute(1, nameof(DialogoCobro.TurnoId), Contexto.Turno.TurnoId);
            b.AddAttribute(2, nameof(DialogoCobro.OnVendida), Microsoft.AspNetCore.Components.EventCallback.Factory.Create<VentaRegistrada>(this, v => vendida = v));
            b.CloseComponent();
            b.OpenComponent<DialogoAutorizacion>(3);
            b.CloseComponent();
        }));

        pantalla.FindAll(".esc-boton--dinero")[0].Click();
        pantalla.WaitForAssertion(() => pantalla.Find("h2").TextContent.ShouldBe("Cobrar"));
        pantalla.WaitForAssertion(() => pantalla.FindAll("h2").Select(h => h.TextContent).ShouldContain("Autoriza un supervisor"));
        pantalla.WaitForAssertion(() => pantalla.Find("[aria-label=Supervisor] .esc-chip").TextContent.ShouldBe("Ramón D."));
        var dialogo = pantalla.FindAll(".esc-velo")[^1];
        foreach (var d in "2468") dialogo.KeyDown(new KeyboardEventArgs { Key = d.ToString() });
        dialogo.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        pantalla.WaitForAssertion(() => vendida.ShouldNotBeNull());
        api.Pedidos.Single(p => p.Ruta == "/api/identidad/autorizaciones").Cuerpo.ShouldContain("ventas.descuento-mayor");
    }

    [Fact]
    public void Un_pin_equivocado_se_explica_sin_salir_de_la_pantalla()
    {
        var (ctx, api) = Contexto.Crear();
        using var _ = ctx;
        ctx.Services.GetRequiredService<Sesion>().Cerrar();
        api.Json("GET", "/api/identidad/usuarios/activos", new[] { new UsuarioActivo(Guid.NewGuid(), "Yudelka M.", "yudelka") })
           .Responder("POST", "/api/identidad/sesiones", _ => ApiFalsa.Problema(HttpStatusCode.Unauthorized, "Identidad.CredencialesInvalidas", "Credenciales inválidas."));
        var entrar = ctx.Render<Entrar>();

        entrar.WaitForAssertion(() => entrar.Find(".esc-mosaico").Click());
        foreach (var d in "1111") entrar.Find("section[aria-label=PIN]").KeyDown(new KeyboardEventArgs { Key = d.ToString() });
        entrar.Find("section[aria-label=PIN]").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        entrar.WaitForAssertion(() => entrar.Find(".esc-aviso").TextContent.ShouldContain("PIN incorrecto."));
        ctx.Services.GetRequiredService<Sesion>().Abierta.ShouldBeFalse();
    }
}

/// <summary>Renders several components side by side, as the layout does with a screen and the supervisor dialog.</summary>
public sealed class Fragmento : Microsoft.AspNetCore.Components.ComponentBase
{
    [Microsoft.AspNetCore.Components.Parameter] public Microsoft.AspNetCore.Components.RenderFragment? Contenido { get; set; }
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, Contenido);
}
