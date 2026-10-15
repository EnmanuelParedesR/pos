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

        var abono = await cajero.Cliente.PostAsJsonAsync($"/api/clientes/{cliente}/abonos", new { TurnoId = cajero.TurnoId, Monto = 350m, Medio = "Efectivo" });
        abono.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await abono.Json()).GetProperty("deudaRestante").GetDecimal().ShouldBe(150m);
        (await (await cajero.Cliente.PostAsJsonAsync($"/api/clientes/{cliente}/abonos", new { TurnoId = cajero.TurnoId, Monto = 151m, Medio = "Efectivo" })).CodigoError())
            .ShouldBe("Clientes.AbonoMayorQueDeuda");

        var x = await app.ReporteXAsync(cajero.TurnoId);
        x.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(350m); // fiado sales bring no cash; the payment does
        x.GetProperty("abonosClientes").GetDecimal().ShouldBe(350m);

        var estado = await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/clientes/{cliente}/estado-cuenta");
        estado.GetProperty("deuda").GetDecimal().ShouldBe(150m);
        var cxc = await (await Admin()).GetFromJsonAsync<JsonElement>("/api/clientes/por-cobrar");
        cxc.EnumerateArray().Single(i => i.GetProperty("clienteId").GetGuid() == cliente).GetProperty("de0a30").GetDecimal().ShouldBe(150m);
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
    public async Task El_cajero_crea_clientes_pero_no_les_da_credito()
    {
        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await cajero.PostAsJsonAsync("/api/clientes", new { Nombre = "Cliente rápido" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await cajero.PostAsJsonAsync("/api/clientes", new { Nombre = "Con crédito", LimiteCredito = 5000m })).CodigoError()).ShouldBe("Clientes.SinPermisoCredito");
    }
}
