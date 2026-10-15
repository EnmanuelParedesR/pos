using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Tienda;

namespace Toki.IntegrationTests.Clientes;

public sealed class ClientesTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Cliente(await app.AdminAsync());

    private async Task<Guid> ClienteAsync(decimal limite = 0, string nivel = "Detalle", string? rnc = null)
    {
        var r = await (await Admin()).PostAsJsonAsync("/api/clientes", new { Nombre = "Doña " + Guid.NewGuid().ToString()[..4], RncCedula = rnc, Nivel = nivel, LimiteCredito = limite });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ProductoAsync(decimal precio = 100m, decimal? mayorista = null)
    {
        var admin = await Admin();
        return await admin.ProductoAsync(await admin.CategoriaAsync(), "Arroz " + Guid.NewGuid().ToString()[..4], precio, tasa: "Exento", existencia: 500, mayorista: mayorista, costo: 50m);
    }

    private static object VentaFiada(Guid turno, Guid cliente, Guid producto, int cantidad, decimal total, params Guid[] autorizaciones) => new
    {
        VentaId = Guid.CreateVersion7(), TurnoId = turno, ClienteId = cliente, Lineas = new[] { Linea(producto, cantidad) },
        Pagos = new[] { new { Medio = "Fiado", Monto = total } }, TotalEsperado = total, Autorizaciones = autorizaciones,
    };

    private static Task<HttpResponseMessage> AbonarAsync(Cajero cajero, Guid cliente, decimal monto, string medio = "Efectivo", Guid? abonoId = null) =>
        cajero.Cliente.PostAsJsonAsync($"/api/clientes/{cliente}/abonos",
            new { AbonoId = abonoId ?? Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Monto = monto, Medio = medio });

    private async Task<decimal> DeudaAsync(Guid cliente) =>
        (await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/clientes/{cliente}")).GetProperty("deuda").GetDecimal();

    private async Task<decimal> SaldoFavorAsync(Guid cliente) =>
        (await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/clientes/{cliente}")).GetProperty("saldoFavor").GetDecimal();

    [Fact]
    public async Task El_fiado_respeta_el_limite_y_por_encima_pide_supervisor()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var cliente = await ClienteAsync(limite: 1000m);
        var arroz = await ProductoAsync();

        (await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, arroz, 6, 600m))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await DeudaAsync(cliente)).ShouldBe(600m);

        var ventaId = Guid.CreateVersion7();
        object Segunda(params Guid[] autorizaciones) => new
        {
            VentaId = ventaId, TurnoId = cajero.TurnoId, ClienteId = cliente, Lineas = new[] { Linea(arroz, 5) },
            Pagos = new[] { new { Medio = "Fiado", Monto = 500m } }, TotalEsperado = 500m, Autorizaciones = autorizaciones,
        };
        (await (await cajero.VenderAsync(Segunda())).CodigoError()).ShouldBe("Autorizacion.Requerida");
        var r = await cajero.VenderAsync(Segunda(await cajero.Cliente.AutorizarAsync("fiado.exceder-limite", ventaId)));
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await DeudaAsync(cliente)).ShouldBe(1100m);
    }

    [Fact]
    public async Task Un_abono_entra_a_la_caja_va_a_las_deudas_mas_viejas_y_no_pasa_de_la_deuda()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 0m);
        var cliente = await ClienteAsync(limite: 5000m);
        var arroz = await ProductoAsync();
        await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, arroz, 3, 300m));
        await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, arroz, 2, 200m));

        var abono = await AbonarAsync(cajero, cliente, 350m);
        abono.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await abono.Json()).GetProperty("deudaRestante").GetDecimal().ShouldBe(150m);
        (await (await AbonarAsync(cajero, cliente, 151m)).CodigoError()).ShouldBe("Clientes.AbonoMayorQueDeuda");

        var x = await app.ReporteXAsync(cajero.TurnoId);
        x.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(350m); // fiado sales bring no cash; the payment does
        x.GetProperty("abonosClientes").GetDecimal().ShouldBe(350m);

        var estado = await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/clientes/{cliente}/estado-cuenta");
        estado.GetProperty("deuda").GetDecimal().ShouldBe(150m);
        var cxc = await (await Admin()).GetFromJsonAsync<JsonElement>("/api/reportes/por-cobrar");
        cxc.EnumerateArray().Single(i => i.GetProperty("clienteId").GetGuid() == cliente).GetProperty("de0a30").GetDecimal().ShouldBe(150m);
        var reporte = await (await Admin()).GetFromJsonAsync<JsonElement>("/api/reportes/por-cobrar");
        reporte.EnumerateArray().Single(i => i.GetProperty("clienteId").GetGuid() == cliente).GetProperty("total").GetDecimal().ShouldBe(150m);
    }

    [Fact]
    public async Task Anular_una_venta_fiada_quita_la_deuda()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var cliente = await ClienteAsync(limite: 5000m);
        var arroz = await ProductoAsync();
        var venta = await (await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, arroz, 4, 400m))).Json();
        var ventaId = venta.GetProperty("ventaId").GetGuid();

        (await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/anular",
            new { Motivo = "equivocación", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.anular", ventaId) })).EnsureSuccessStatusCode();
        (await DeudaAsync(cliente)).ShouldBe(0m);
    }

    [Fact]
    public async Task Una_devolucion_a_la_cuenta_baja_la_deuda_de_esa_venta_y_el_resto_queda_a_favor()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var cliente = await ClienteAsync(limite: 5000m);
        var arroz = await ProductoAsync();
        var venta = await (await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, ClienteId = cliente, Lineas = new[] { Linea(arroz, 5) }, TotalEsperado = 500m,
            Pagos = new object[] { new { Medio = "Efectivo", Monto = 300m }, new { Medio = "Fiado", Monto = 200m } },
        })).Json();
        var ventaId = venta.GetProperty("ventaId").GetGuid();
        var linea = (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}")).GetProperty("lineas")[0].GetProperty("id").GetGuid();

        var dev = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/devoluciones", new
        {
            DevolucionId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Medio = "SaldoFavor", Motivo = "sobró",
            Lineas = new[] { new { LineaVentaId = linea, Cantidad = 3m } }, AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.devolver", ventaId),
        });
        dev.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DeudaAsync(cliente)).ShouldBe(0m);      // 300 returned: 200 pays the debt of that sale
        (await SaldoFavorAsync(cliente)).ShouldBe(100m); // the other 100 stays in favor

        // The balance in favor pays the next sale; it cannot be overdrawn.
        (await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, ClienteId = cliente, Lineas = new[] { Linea(arroz, 2) }, TotalEsperado = 200m,
            Pagos = new object[] { new { Medio = "SaldoFavor", Monto = 100m }, new { Medio = "Efectivo", Monto = 100m } },
        })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SaldoFavorAsync(cliente)).ShouldBe(0m);
        var sinSaldo = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, ClienteId = cliente, Lineas = new[] { Linea(arroz, 1) }, TotalEsperado = 100m,
            Pagos = new[] { new { Medio = "SaldoFavor", Monto = 100m } },
        });
        (await sinSaldo.CodigoError()).ShouldBe("Clientes.SinSaldoFavor");
    }

    [Fact]
    public async Task Un_cliente_mayorista_compra_a_precio_mayorista_sin_supervisor()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var cliente = await ClienteAsync(nivel: "Mayorista");
        var arroz = await ProductoAsync(precio: 100m, mayorista: 85m);
        var r = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, ClienteId = cliente, Nivel = "Mayorista", Lineas = new[] { Linea(arroz, 10) },
            Pagos = new[] { new { Medio = "Efectivo", Monto = 850m } }, TotalEsperado = 850m,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);

        var otro = await ClienteAsync();
        (await (await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, ClienteId = otro, Nivel = "Mayorista", Lineas = new[] { Linea(arroz, 10) },
            Pagos = new[] { new { Medio = "Efectivo", Monto = 850m } }, TotalEsperado = 850m,
        })).CodigoError()).ShouldBe("Autorizacion.Requerida");
    }

    [Fact]
    public async Task El_credito_fiscal_toma_el_RNC_del_cliente()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var cliente = await ClienteAsync(rnc: "130" + Random.Shared.Next(100000, 999999));
        var arroz = await ProductoAsync();
        var r = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, ClienteId = cliente, TipoComprobante = "CreditoFiscal31", Lineas = new[] { Linea(arroz, 1) },
            Pagos = new[] { new { Medio = "Efectivo", Monto = 100m } }, TotalEsperado = 100m,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await r.Json()).GetProperty("comprobante").GetProperty("rncCliente").GetString()!.Length.ShouldBe(9);
    }

    [Fact]
    public async Task El_cajero_crea_clientes_pero_no_les_da_credito_ni_reactiva_bloqueados()
    {
        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await cajero.PostAsJsonAsync("/api/clientes", new { Nombre = "Cliente rápido" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await cajero.PostAsJsonAsync("/api/clientes", new { Nombre = "Con crédito", LimiteCredito = 5000m })).CodigoError()).ShouldBe("Clientes.SinPermisoCredito");

        // The owner blocks a customer who does not pay; a cashier cannot unblock them, nor raise the limit or set wholesale.
        var moroso = await ClienteAsync(limite: 1000m);
        (await (await Admin()).PutAsJsonAsync($"/api/clientes/{moroso}", new { Nombre = "Moroso", LimiteCredito = 1000m, Activo = false })).EnsureSuccessStatusCode();
        (await (await cajero.PutAsJsonAsync($"/api/clientes/{moroso}", new { Nombre = "Moroso", LimiteCredito = 1000m, Activo = true })).CodigoError())
            .ShouldBe("Clientes.SinPermisoCredito");
        (await (await cajero.PutAsJsonAsync($"/api/clientes/{moroso}", new { Nombre = "Moroso", LimiteCredito = 1000m, Nivel = "Mayorista", Activo = false })).CodigoError())
            .ShouldBe("Clientes.SinPermisoCredito");

        // Blocked customers only show up for whoever can reactivate them.
        (await cajero.GetFromJsonAsync<JsonElement>("/api/clientes?buscar=Moroso&inactivos=true")).EnumerateArray().ShouldNotContain(c => c.GetProperty("id").GetGuid() == moroso);
        (await (await Admin()).GetFromJsonAsync<JsonElement>("/api/clientes?buscar=Moroso&inactivos=true")).EnumerateArray().ShouldContain(c => c.GetProperty("id").GetGuid() == moroso);
        (await cajero.GetAsync("/api/reportes/por-cobrar")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Repetir_el_mismo_abono_no_lo_cobra_dos_veces()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 0m);
        var cliente = await ClienteAsync(limite: 5000m);
        await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, await ProductoAsync(), 10, 1000m));
        var id = Guid.CreateVersion7();

        var respuestas = await Task.WhenAll(AbonarAsync(cajero, cliente, 500m, abonoId: id), AbonarAsync(cajero, cliente, 500m, abonoId: id));
        respuestas.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await DeudaAsync(cliente)).ShouldBe(500m);
        (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("efectivoEsperado").GetDecimal().ShouldBe(500m);
    }

    [Fact]
    public async Task Un_abono_en_efectivo_es_en_pesos_enteros_y_los_centavos_quedan_como_redondeo()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 0m);
        var cliente = await ClienteAsync(limite: 5000m);
        await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, await ProductoAsync(388.58m), 1, 388.58m));

        (await AbonarAsync(cajero, cliente, 388.58m)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // no coins under RD$ 1
        (await (await AbonarAsync(cajero, cliente, 390m)).CodigoError()).ShouldBe("Clientes.AbonoMayorQueDeuda");
        var r = await AbonarAsync(cajero, cliente, 389m);
        (await r.Json()).GetProperty("aplicado").GetDecimal().ShouldBe(388.58m);
        (await DeudaAsync(cliente)).ShouldBe(0m);
        var x = await app.ReporteXAsync(cajero.TurnoId);
        x.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(389m);
        x.GetProperty("redondeo").GetDecimal().ShouldBe(0.42m);
    }

    [Fact]
    public async Task Anular_una_venta_fiada_ya_pagada_en_parte_paga_otras_deudas_y_el_estado_de_cuenta_cuadra()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 0m);
        var arroz = await ProductoAsync();
        async Task<Guid> Fiar(Guid cliente, int cantidad) =>
            (await (await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, arroz, cantidad, cantidad * 100m))).Json()).GetProperty("ventaId").GetGuid();
        async Task Anular(Guid venta) => (await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{venta}/anular",
            new { Motivo = "error", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.anular", venta) })).EnsureSuccessStatusCode();
        async Task<JsonElement> Estado(Guid cliente) => await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/clientes/{cliente}/estado-cuenta");

        // Only that sale: the 400 paid becomes balance in favor; the statement ends at 0 (not −400).
        var solo = await ClienteAsync(limite: 5000m);
        var venta = await Fiar(solo, 10);
        await AbonarAsync(cajero, solo, 400m);
        await Anular(venta);
        var estado = await Estado(solo);
        estado.GetProperty("deuda").GetDecimal().ShouldBe(0m);
        estado.GetProperty("movimientos").EnumerateArray().Last().GetProperty("deuda").GetDecimal().ShouldBe(0m);
        estado.GetProperty("saldoFavor").GetDecimal().ShouldBe(400m);

        // With an older debt of 2,000: the 400 pays it down instead of sitting in favor.
        var otro = await ClienteAsync(limite: 5000m);
        await Fiar(otro, 20);
        var segunda = await Fiar(otro, 10);
        await AbonarAsync(cajero, otro, 2400m); // pays the 2,000 and 400 of the second sale
        await Anular(segunda);
        (await DeudaAsync(otro)).ShouldBe(0m);
        (await SaldoFavorAsync(otro)).ShouldBe(400m);

        // The voided sale is the oldest, so the payment went to it; voiding it moves that 400 to the newer 2,000.
        var tercero = await ClienteAsync(limite: 5000m);
        var vieja = await Fiar(tercero, 10);
        await Fiar(tercero, 20);
        var abono = await (await AbonarAsync(cajero, tercero, 400m)).Json();
        abono.GetProperty("deudaRestante").GetDecimal().ShouldBe(2600m);
        await Anular(vieja);
        estado = await Estado(tercero);
        estado.GetProperty("deuda").GetDecimal().ShouldBe(1600m);
        estado.GetProperty("movimientos").EnumerateArray().Last().GetProperty("deuda").GetDecimal().ShouldBe(1600m);
        estado.GetProperty("saldoFavor").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task Una_devolucion_a_la_cuenta_paga_primero_las_deudas_viejas()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var cliente = await ClienteAsync(limite: 5000m);
        var arroz = await ProductoAsync();
        await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, arroz, 20, 2000m));
        var contado = (await (await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, ClienteId = cliente, Lineas = new[] { Linea(arroz, 5) }, TotalEsperado = 500m,
            Pagos = new[] { new { Medio = "Efectivo", Monto = 500m } },
        })).Json()).GetProperty("ventaId").GetGuid();
        var linea = (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{contado}")).GetProperty("lineas")[0].GetProperty("id").GetGuid();

        (await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{contado}/devoluciones", new
        {
            DevolucionId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Medio = "SaldoFavor", Motivo = "sobró",
            Lineas = new[] { new { LineaVentaId = linea, Cantidad = 5m } }, AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.devolver", contado),
        })).EnsureSuccessStatusCode();
        (await DeudaAsync(cliente)).ShouldBe(1500m);
        (await SaldoFavorAsync(cliente)).ShouldBe(0m);
    }

    [Fact]
    public async Task Un_abono_mal_escrito_se_anula_con_supervisor_y_sale_de_la_caja()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 0m);
        var cliente = await ClienteAsync(limite: 10000m);
        await cajero.VenderAsync(VentaFiada(cajero.TurnoId, cliente, await ProductoAsync(), 60, 6000m));
        var abono = (await (await AbonarAsync(cajero, cliente, 5000m, "Tarjeta")).Json()).GetProperty("abonoId").GetGuid();
        (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("porMedio").EnumerateArray()
            .Single(m => m.GetProperty("medio").GetString() == "Tarjeta").GetProperty("abonos").GetDecimal().ShouldBe(5000m);

        (await (await cajero.Cliente.PostAsJsonAsync($"/api/clientes/abonos/{abono}/anular", new { Motivo = "era 500" })).CodigoError()).ShouldBe("Autorizacion.Requerida");
        (await cajero.Cliente.PostAsJsonAsync($"/api/clientes/abonos/{abono}/anular",
            new { Motivo = "era 500", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("fiado.gestionar", abono) })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DeudaAsync(cliente)).ShouldBe(6000m);
        var x = await app.ReporteXAsync(cajero.TurnoId);
        x.GetProperty("abonosClientes").GetDecimal().ShouldBe(0m);
        (await (await cajero.Cliente.PostAsJsonAsync($"/api/clientes/abonos/{abono}/anular",
            new { Motivo = "otra vez", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("fiado.gestionar", abono) })).CodigoError()).ShouldBe("Clientes.AbonoYaAnulado");
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }
}
