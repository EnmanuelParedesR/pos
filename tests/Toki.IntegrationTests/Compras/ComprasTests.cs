using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Compras;

public sealed class ComprasTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Cliente(await app.AdminAsync());

    private static string NcfNuevo() => "B01" + Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task<Guid> SuplidorAsync(HttpClient c, int dias = 30) =>
        (await (await c.PostAsJsonAsync("/api/compras/suplidores", new { Nombre = "Granja " + Guid.NewGuid().ToString()[..4], Rnc = "101234567", DiasCredito = dias })).Json())
        .GetProperty("id").GetGuid();

    private static async Task<Guid> CompraAsync(HttpClient c, Guid suplidor, string condicion, DateOnly fecha, params object[] lineas)
    {
        var r = await c.PostAsJsonAsync("/api/compras", new { SuplidorId = suplidor, Ncf = NcfNuevo(), FechaFactura = fecha, Condicion = condicion, Lineas = lineas });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await r.Json()).GetProperty("id").GetGuid();
        (await c.PostAsync($"/api/compras/{id}/aplicar", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return id;
    }

    [Fact]
    public async Task Aplicar_una_compra_entra_existencia_y_recalcula_el_costo()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync(), "Pierna de cerdo", 150m, unidad: "Libra", costo: 80m, existencia: 20);
        var suplidor = await SuplidorAsync(c);

        var borrador = await (await c.PostAsJsonAsync("/api/compras", new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), FechaFactura = "2026-10-15", Condicion = "Contado",
            Lineas = new[] { new { ProductoId = producto, Cantidad = 60m, CostoUnitario = 100m, TasaItbis = "Exento" } },
        })).Json();
        borrador.GetProperty("total").GetDecimal().ShouldBe(6000m);
        var id = borrador.GetProperty("id").GetGuid();
        (await c.ExistenciaAsync(producto)).ShouldBe(20m); // a draft moves nothing

        (await c.PostAsync($"/api/compras/{id}/aplicar", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await c.PostAsync($"/api/compras/{id}/aplicar", null)).CodigoError()).ShouldBe("Compras.NoEsBorrador");

        (await c.ExistenciaAsync(producto)).ShouldBe(80m);
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{producto}")).GetProperty("costo").GetDecimal().ShouldBe(95m); // (20×80 + 60×100)/80
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Comprar_por_caja_entra_unidades_y_el_costo_por_unidad()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync("Bebidas"), "Agua 500ml", 25m, costo: 0m);
        var caja = (await (await c.PostAsJsonAsync($"/api/inventario/productos/{producto}/presentaciones", new { Nombre = "Fardo 24", Factor = 24m, Precio = 500m })).Json()).GetProperty("id").GetGuid();
        await CompraAsync(c, await SuplidorAsync(c), "Contado", new DateOnly(2026, 10, 15),
            new { ProductoId = producto, PresentacionId = caja, Cantidad = 2m, CostoUnitario = 312m });

        (await c.ExistenciaAsync(producto)).ShouldBe(48m);
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{producto}")).GetProperty("costo").GetDecimal().ShouldBe(13m);
    }

    [Fact]
    public async Task El_mismo_NCF_del_mismo_suplidor_no_entra_dos_veces()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync(), "Sal", 20m);
        var suplidor = await SuplidorAsync(c);
        var ncf = NcfNuevo();
        object Cuerpo() => new { SuplidorId = suplidor, Ncf = ncf, FechaFactura = "2026-10-15", Condicion = "Contado", Lineas = new[] { new { ProductoId = producto, Cantidad = 1m, CostoUnitario = 10m } } };
        (await c.PostAsJsonAsync("/api/compras", Cuerpo())).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await c.PostAsJsonAsync("/api/compras", Cuerpo())).CodigoError()).ShouldBe("Compras.NcfRepetido");
    }

    [Fact]
    public async Task Los_pagos_van_a_las_facturas_mas_viejas_y_no_pasan_de_la_deuda()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync(), "Pollo entero", 95m, unidad: "Libra");
        var suplidor = await SuplidorAsync(c, dias: 15);
        var vieja = await CompraAsync(c, suplidor, "Credito", new DateOnly(2026, 9, 1), new { ProductoId = producto, Cantidad = 100m, CostoUnitario = 60m, TasaItbis = "Exento" });
        await CompraAsync(c, suplidor, "Credito", new DateOnly(2026, 10, 1), new { ProductoId = producto, Cantidad = 50m, CostoUnitario = 60m, TasaItbis = "Exento" });

        var demasiado = await c.PostAsJsonAsync("/api/compras/pagos", new { SuplidorId = suplidor, Monto = 9001m, Medio = "Transferencia", Fecha = "2026-10-15" });
        (await demasiado.CodigoError()).ShouldBe("Compras.PagoMayorQueDeuda");

        var pago = await (await c.PostAsJsonAsync("/api/compras/pagos", new { SuplidorId = suplidor, Monto = 7000m, Medio = "Transferencia", Fecha = "2026-10-15", Referencia = "TRF-1" })).Json();
        var aplicaciones = pago.GetProperty("aplicaciones");
        aplicaciones[0].GetProperty("monto").GetDecimal().ShouldBe(6000m); // the September invoice first, in full
        aplicaciones[1].GetProperty("monto").GetDecimal().ShouldBe(1000m);

        var estado = await c.GetFromJsonAsync<JsonElement>($"/api/compras/suplidores/{suplidor}/estado-cuenta");
        estado.GetProperty("saldo").GetDecimal().ShouldBe(2000m);
        var antiguedad = await c.GetFromJsonAsync<JsonElement>("/api/reportes/por-pagar");
        antiguedad.EnumerateArray().Single(i => i.GetProperty("suplidorId").GetGuid() == suplidor).GetProperty("total").GetDecimal().ShouldBe(2000m);

        // A purchase with payments cannot be voided.
        (await (await c.PostAsJsonAsync($"/api/compras/{vieja}/anular", new { Motivo = "error" })).CodigoError()).ShouldBe("Compras.ConPagos");
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Anular_una_compra_saca_la_existencia_pero_no_por_debajo_de_cero()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync(), "Longaniza", 200m, unidad: "Libra", venderSinExistencia: true);
        var suplidor = await SuplidorAsync(c);
        var compra = await CompraAsync(c, suplidor, "Contado", new DateOnly(2026, 10, 15), new { ProductoId = producto, Cantidad = 10m, CostoUnitario = 120m });
        (await app.ComandoAsync(new MoverInventario(producto, 4m, Entrada: false))).IsSuccess.ShouldBeTrue();

        (await (await c.PostAsJsonAsync($"/api/compras/{compra}/anular", new { Motivo = "llegó mal" })).CodigoError()).ShouldBe("Inventario.SinExistencia");
        (await c.ExistenciaAsync(producto)).ShouldBe(6m);

        var otra = await CompraAsync(c, suplidor, "Contado", new DateOnly(2026, 10, 15), new { ProductoId = producto, Cantidad = 5m, CostoUnitario = 120m });
        (await c.PostAsJsonAsync($"/api/compras/{otra}/anular", new { Motivo = "duplicada" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.ExistenciaAsync(producto)).ShouldBe(6m);
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_gasto_a_credito_es_una_cuenta_por_pagar()
    {
        var c = await Admin();
        var suplidor = await SuplidorAsync(c);
        var r = await c.PostAsJsonAsync("/api/compras/gastos", new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), Fecha = "2026-10-10", Concepto = "Energía eléctrica", Tipo = "TrabajosSuministrosServicios",
            Subtotal = 4000m, Itbis = 720m, Condicion = "Credito",
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await c.GetFromJsonAsync<JsonElement>($"/api/compras/suplidores/{suplidor}/estado-cuenta")).GetProperty("saldo").GetDecimal().ShouldBe(4720m);

        var malo = await c.PostAsJsonAsync("/api/compras/gastos", new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), Fecha = "2026-10-10", Concepto = "x", Tipo = "GastosFinancieros", Subtotal = 100m, Itbis = 50m, Condicion = "Contado",
        });
        malo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Anular_una_compra_con_un_error_de_tipeo_devuelve_el_costo_y_dos_anulaciones_sacan_una_vez()
    {
        var c = await Admin();
        var chuleta = await c.ProductoAsync(await c.CategoriaAsync(), "Chuleta " + Guid.NewGuid().ToString()[..4], 165m, unidad: "Libra", tasa: "Exento", costo: 100m, existencia: 50);
        // 1,000 typed instead of 100: the average jumps to (50×100 + 100×1,000) / 150 = 700.
        var compra = await CompraAsync(c, await SuplidorAsync(c), "Contado", new DateOnly(2026, 10, 15),
            new { ProductoId = chuleta, Cantidad = 100m, CostoUnitario = 1000m, TasaItbis = "Exento" });
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{chuleta}")).GetProperty("costo").GetDecimal().ShouldBe(700m);

        var respuestas = await Task.WhenAll(
            c.PostAsJsonAsync($"/api/compras/{compra}/anular", new { Motivo = "costo mal escrito" }),
            c.PostAsJsonAsync($"/api/compras/{compra}/anular", new { Motivo = "costo mal escrito" }));
        respuestas.Count(r => r.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
        (await respuestas.Single(r => r.StatusCode != HttpStatusCode.NoContent).CodigoError()).ShouldBe("Compras.YaAnulada");

        (await c.ExistenciaAsync(chuleta)).ShouldBe(50m);
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{chuleta}")).GetProperty("costo").GetDecimal().ShouldBe(100m);
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Las_facturas_no_se_duplican_y_un_reintento_devuelve_el_mismo_borrador()
    {
        var c = await Admin();
        var sal = await c.ProductoAsync(await c.CategoriaAsync(), "Sal " + Guid.NewGuid().ToString()[..4], 20m);
        var suplidor = await SuplidorAsync(c);
        var ncf = NcfNuevo();
        var id = Guid.CreateVersion7();
        object Compra(Guid? clave) => new
        {
            Id = clave, SuplidorId = suplidor, Ncf = ncf, FechaFactura = "2026-10-15", Condicion = "Contado",
            Lineas = new[] { new { ProductoId = sal, Cantidad = 1m, CostoUnitario = 10m } },
        };
        var primera = await (await c.PostAsJsonAsync("/api/compras", Compra(id))).Json();
        var segunda = await c.PostAsJsonAsync("/api/compras", Compra(id));
        segunda.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await segunda.Json()).GetProperty("id").GetGuid().ShouldBe(primera.GetProperty("id").GetGuid());

        // The same NCF cannot come back as an expense either.
        var gasto = await c.PostAsJsonAsync("/api/compras/gastos", new
        {
            SuplidorId = suplidor, Ncf = ncf, Fecha = "2026-10-15", Concepto = "x", Tipo = "GastosFinancieros", Subtotal = 100m, Itbis = 0m, Condicion = "Contado",
        });
        (await gasto.CodigoError()).ShouldBe("Compras.NcfRepetido");
    }

    [Fact]
    public async Task El_ITBIS_de_la_factura_manda_si_esta_a_menos_de_un_peso()
    {
        var c = await Admin();
        var cat = await c.CategoriaAsync();
        var (a, b) = (await c.ProductoAsync(cat, "Aceite " + Guid.NewGuid().ToString()[..4], 300m), await c.ProductoAsync(cat, "Harina " + Guid.NewGuid().ToString()[..4], 60m));
        var suplidor = await SuplidorAsync(c);
        // Line math: 3 × 33.33 = 99.99 → ITBIS 18.00; 1 × 10.55 → 1.90. Total 130.44. The invoice says ITBIS 19.89, total 130.43.
        object Compra(decimal itbis, decimal total) => new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), FechaFactura = "2026-10-15", Condicion = "Credito", ItbisFactura = itbis, TotalFactura = total,
            Lineas = new object[] { new { ProductoId = a, Cantidad = 3m, CostoUnitario = 33.33m }, new { ProductoId = b, Cantidad = 1m, CostoUnitario = 10.55m } },
        };
        var r = await (await c.PostAsJsonAsync("/api/compras", Compra(19.89m, 130.43m))).Json();
        (r.GetProperty("itbis").GetDecimal(), r.GetProperty("total").GetDecimal()).ShouldBe((19.89m, 130.43m));
        (await (await c.PostAsJsonAsync("/api/compras", Compra(25m, 136m))).CodigoError()).ShouldBe("Compras.TotalFacturaLejos");
    }

    [Fact]
    public async Task Gastos_y_pagos_mal_escritos_se_anulan()
    {
        var c = await Admin();
        var suplidor = await SuplidorAsync(c);
        var ncf = NcfNuevo();
        object Gasto(decimal subtotal) => new
        {
            SuplidorId = suplidor, Ncf = ncf, Fecha = "2026-10-10", Concepto = "Alquiler", Tipo = "Arrendamientos", Subtotal = subtotal, Itbis = 0m, Condicion = "Credito",
        };
        var malo = (await (await c.PostAsJsonAsync("/api/compras/gastos", Gasto(40000m))).Json()).GetProperty("id").GetGuid();
        (await c.PostAsJsonAsync($"/api/compras/gastos/{malo}/anular", new { Motivo = "era 4,000" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.PostAsJsonAsync("/api/compras/gastos", Gasto(4000m))).StatusCode.ShouldBe(HttpStatusCode.Created); // the NCF is free again

        async Task<decimal> Saldo() => (await c.GetFromJsonAsync<JsonElement>($"/api/compras/suplidores/{suplidor}/estado-cuenta")).GetProperty("saldo").GetDecimal();
        (await Saldo()).ShouldBe(4000m);
        var pago = (await (await c.PostAsJsonAsync("/api/compras/pagos", new { SuplidorId = suplidor, Monto = 4000m, Medio = "Transferencia", Fecha = "2026-10-15" })).Json())
            .GetProperty("pagoId").GetGuid();
        (await Saldo()).ShouldBe(0m);
        (await c.PostAsJsonAsync($"/api/compras/pagos/{pago}/anular", new { Motivo = "era otro suplidor" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Saldo()).ShouldBe(4000m);
        (await (await c.PostAsJsonAsync($"/api/compras/pagos/{pago}/anular", new { Motivo = "otra vez" })).CodigoError()).ShouldBe("Compras.YaAnulada");
    }

    [Fact]
    public async Task Pagarle_al_suplidor_con_efectivo_de_la_caja_sale_del_turno()
    {
        var c = await Admin();
        var cajero = await app.CajeroConTurnoAsync(fondo: 10000m);
        var suplidor = await SuplidorAsync(c);
        (await c.PostAsJsonAsync("/api/compras/gastos", new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), Fecha = "2026-10-10", Concepto = "Transporte", Tipo = "TrabajosSuministrosServicios", Subtotal = 5000m, Itbis = 0m, Condicion = "Credito",
        })).EnsureSuccessStatusCode();

        var pagoId = Guid.CreateVersion7();
        object Pago() => new { PagoId = pagoId, SuplidorId = suplidor, Monto = 5000m, Medio = "Efectivo", Fecha = "2026-10-15", TurnoId = cajero.TurnoId };
        (await c.PostAsJsonAsync("/api/compras/pagos", Pago())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.PostAsJsonAsync("/api/compras/pagos", Pago())).StatusCode.ShouldBe(HttpStatusCode.OK); // a retry pays once
        (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("efectivoEsperado").GetDecimal().ShouldBe(5000m);

        (await c.PostAsJsonAsync($"/api/compras/pagos/{pagoId}/anular", new { Motivo = "no era a este" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("efectivoEsperado").GetDecimal().ShouldBe(10000m);
    }

    [Fact]
    public async Task Fechas_futuras_y_suplidores_sin_RNC_se_rechazan_y_la_antiguedad_usa_el_vencimiento()
    {
        var c = await Admin();
        var sinRnc = (await (await c.PostAsJsonAsync("/api/compras/suplidores", new { Nombre = "Sin RNC " + Guid.NewGuid().ToString()[..4], DiasCredito = 0 })).Json())
            .GetProperty("id").GetGuid();
        object Gasto(Guid suplidor, DateOnly fecha, int dias) => new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), Fecha = fecha, Concepto = "Luz", Tipo = "TrabajosSuministrosServicios", Subtotal = 100m, Itbis = 0m,
            Condicion = "Credito", DiasCredito = dias,
        };
        var hoy = DateOnly.FromDateTime(app.Reloj.GetUtcNow().UtcDateTime.AddHours(-4));
        (await (await c.PostAsJsonAsync("/api/compras/gastos", Gasto(sinRnc, hoy, 0))).CodigoError()).ShouldBe("Compras.SuplidorSinRnc");

        var suplidor = await SuplidorAsync(c);
        (await c.PostAsJsonAsync("/api/compras/gastos", Gasto(suplidor, hoy.AddYears(1), 0))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await c.PostAsJsonAsync("/api/compras/gastos", Gasto(suplidor, hoy, 0))).EnsureSuccessStatusCode();                // due today: not overdue
        (await c.PostAsJsonAsync("/api/compras/gastos", Gasto(suplidor, hoy.AddDays(-1), 0))).EnsureSuccessStatusCode();    // due yesterday: 1–30
        var fila = (await c.GetFromJsonAsync<JsonElement>("/api/reportes/por-pagar")).EnumerateArray().Single(i => i.GetProperty("suplidorId").GetGuid() == suplidor);
        (fila.GetProperty("porVencer").GetDecimal(), fila.GetProperty("vencido1a30").GetDecimal()).ShouldBe((100m, 100m));
    }

    [Fact]
    public async Task El_606_trae_compras_aplicadas_y_gastos_y_por_pagar_se_exporta()
    {
        var c = await Admin();
        var suplidor = await SuplidorAsync(c);
        var res = await c.ProductoAsync(await c.CategoriaAsync(), "Res " + Guid.NewGuid().ToString()[..4], 250m, unidad: "Libra", tasa: "Exento");
        var compra = await CompraAsync(c, suplidor, "Credito", new DateOnly(2026, 10, 14), new { ProductoId = res, Cantidad = 10m, CostoUnitario = 180m });
        var ncfCompra = (await c.GetFromJsonAsync<JsonElement>($"/api/compras/{compra}")).GetProperty("ncf").GetString();
        var ncfGasto = NcfNuevo();
        (await c.PostAsJsonAsync("/api/compras/gastos", new
        {
            SuplidorId = suplidor, Ncf = ncfGasto, Fecha = "2026-10-14", Concepto = "Hielo", Tipo = "TrabajosSuministrosServicios", Subtotal = 500m, Itbis = 90m, Condicion = "Contado",
        })).EnsureSuccessStatusCode();

        var filas = (await c.GetFromJsonAsync<JsonElement>("/api/reportes/dgii/606?desde=2026-10-14&hasta=2026-10-14")).EnumerateArray().ToList();
        var deCompra = filas.Single(f => f.GetProperty("ncf").GetString() == ncfCompra);
        (deCompra.GetProperty("tipoBienesServicios").GetInt32(), deCompra.GetProperty("montoBienes").GetDecimal(), deCompra.GetProperty("tipoId").GetInt32()).ShouldBe((9, 1800m, 1));
        filas.Single(f => f.GetProperty("ncf").GetString() == ncfGasto).GetProperty("itbisFacturado").GetDecimal().ShouldBe(90m);

        var csv = await c.GetStringAsync("/api/reportes/por-pagar?formato=csv");
        csv.ShouldContain(suplidor.ToString());
    }

    [Fact]
    public async Task Un_cajero_no_puede_registrar_compras()
    {
        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await cajero.GetAsync("/api/compras")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
