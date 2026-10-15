using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Toki.E2ETests;

/// <summary>TK-095: a whole store day against the real process over HTTP. Three registers sell at the same time, the server
/// restarts in the middle of the day, and at the end every total must add up: drawers, kardex, e-NCF numbers, documents,
/// customer and supplier balances, and the audit chain. No request may answer 500.</summary>
public sealed class DiaDeTiendaTests
{
    private const string PinAdmin = "1234", PinSupervisor = "2468", PinCajero = "4321";
    private static readonly Guid RolSupervisor = new("0199e000-0000-7000-8000-000000000002"), RolCajero = new("0199e000-0000-7000-8000-000000000003");
    private static readonly decimal[] Billetes = [2000, 1000, 500, 200, 100, 50, 25, 10, 5, 1];

    /// <summary>A register: its device, cashier session, shift, and the cash the test expects in its drawer (an independent model of the server's).</summary>
    private sealed class Caja(string nombre, HttpClient cliente)
    {
        public string Nombre { get; } = nombre;
        public HttpClient Cliente { get; } = cliente;
        public Guid CajaId { get; set; }
        public Guid TurnoId { get; set; }
        public decimal Efectivo { get; set; }
        public List<(Guid VentaId, decimal Total, decimal EfectivoQueQuedo)> Ventas { get; } = [];
    }

    [Fact]
    public async Task Un_dia_completo_de_tienda()
    {
        await using var servidor = await ServidorToki.IniciarAsync();
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-4));

        // ── Installation: owner PC, three paired registers, users ──
        var instalacion = await Ok(await servidor.Cliente().PostAsJsonAsync("/api/identidad/instalacion",
            new { NombreAdministrador = "Dueña", CodigoAdministrador = "admin", Pin = PinAdmin, NombreDispositivo = "PC de la oficina" }));
        var equipoOficina = instalacion.GetProperty("tokenDispositivo").GetString()!;
        var admin = servidor.Cliente(equipoOficina, await SesionAsync(servidor, equipoOficina, "admin", PinAdmin));
        await Ok(await admin.PostAsJsonAsync("/api/identidad/usuarios", new { Nombre = "Supervisora", Codigo = "sup", Pin = PinSupervisor, RolId = RolSupervisor }));
        await Ok(await admin.PutAsJsonAsync("/api/configuracion/negocio", new { Nombre = "Carnicería La Esquina", Rnc = "131245678", TopeDescuentoPct = 10m }));
        // Three registers need a license (a trial allows two).
        await Ok(await admin.PutAsJsonAsync("/api/configuracion/licencia", new { Licencia = LicenciaFirmada(servidor.ClaveLicencias, cajas: 3) }));
        foreach (var tipo in new[] { 31, 32, 34 })
            await Ok(await admin.PostAsJsonAsync("/api/fiscal/secuencias", new { Tipo = tipo, Desde = 1, Hasta = 10_000, VenceEl = hoy.AddYears(1) }));

        var cajas = new List<Caja>();
        for (var i = 1; i <= 3; i++)
        {
            var codigo = (await Ok(await admin.PostAsJsonAsync("/api/identidad/dispositivos/codigos", new { NombreDispositivo = $"Caja {i}" }))).GetProperty("codigo").GetString();
            var equipo = (await Ok(await servidor.Cliente().PostAsJsonAsync("/api/identidad/dispositivos/emparejar", new { Codigo = codigo }))).GetProperty("token").GetString()!;
            await Ok(await admin.PostAsJsonAsync("/api/identidad/usuarios", new { Nombre = $"Cajero {i}", Codigo = $"caj{i}", Pin = PinCajero, RolId = RolCajero }));
            var caja = new Caja($"Caja {i}", servidor.Cliente(equipo, await SesionAsync(servidor, equipo, $"caj{i}", PinCajero)));
            caja.CajaId = (await Ok(await admin.PostAsJsonAsync("/api/caja/cajas", new { Nombre = caja.Nombre }))).GetProperty("id").GetGuid();
            cajas.Add(caja);
        }

        // ── Catalog and the morning's purchases (the day sells at the new average cost) ──
        var carnes = (await Ok(await admin.PostAsJsonAsync("/api/inventario/categorias", new { Nombre = "Carnes", Color = "#B42318", Notas = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        var colmado = (await Ok(await admin.PostAsJsonAsync("/api/inventario/categorias", new { Nombre = "Colmado", Color = "#0B5CAD", Notas = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        async Task<Guid> Producto(string nombre, Guid categoria, decimal precio, string unidad, string tasa, decimal? mayorista = null, string? plu = null) =>
            (await Ok(await admin.PostAsJsonAsync("/api/inventario/productos", new
            {
                Nombre = nombre, CategoriaId = categoria, Unidad = unidad, Plu = plu, TasaItbis = tasa, Precio = precio, PrecioMayorista = mayorista, Costo = 0m,
                VenderSinExistencia = false, StockMinimo = 0, Codigos = Array.Empty<string>(), ExistenciaInicial = 0m,
            }))).GetProperty("id").GetGuid();
        var chuleta = await Producto("Chuleta", carnes, 165m, "Libra", "Exento", plu: "1234");
        var refresco = await Producto("Refresco 2L", colmado, 59m, "Unidad", "General18");
        var arroz = await Producto("Arroz 1 lb", colmado, 50m, "Unidad", "Exento", mayorista: 45m);
        var queso = await Producto("Queso de freír", colmado, 50m, "Unidad", "General18");
        var fardo = (await Ok(await admin.PostAsJsonAsync($"/api/inventario/productos/{refresco}/presentaciones", new { Nombre = "Fardo 8", Factor = 8m, Precio = 440m }))).GetProperty("id").GetGuid();
        var productos = new[] { chuleta, refresco, arroz, queso };

        var suplidor = (await Ok(await admin.PostAsJsonAsync("/api/compras/suplidores", new { Nombre = "Distribuidora del Cibao", Rnc = "101234567", DiasCredito = 15 }))).GetProperty("id").GetGuid();
        async Task<Guid> Comprar(string condicion, params object[] lineas)
        {
            var id = (await Ok(await admin.PostAsJsonAsync("/api/compras", new
            {
                Id = Guid.CreateVersion7(), SuplidorId = suplidor, Ncf = "B01" + Random.Shared.Next(10_000_000, 99_999_999), FechaFactura = hoy, Condicion = condicion, Lineas = lineas,
            }))).GetProperty("id").GetGuid();
            await Ok(await admin.PostAsync($"/api/compras/{id}/aplicar", null));
            return id;
        }
        await Comprar("Credito", new { ProductoId = chuleta, Cantidad = 300m, CostoUnitario = 110m });
        await Comprar("Contado", new { ProductoId = refresco, PresentacionId = fardo, Cantidad = 40m, CostoUnitario = 280m }, new { ProductoId = arroz, Cantidad = 500m, CostoUnitario = 30m },
            new { ProductoId = queso, Cantidad = 100m, CostoUnitario = 25m });
        // A typo (250 instead of 25), voided: the cost must come back to 25.
        var typo = await Comprar("Contado", new { ProductoId = queso, Cantidad = 50m, CostoUnitario = 250m });
        await Ok(await admin.PostAsJsonAsync($"/api/compras/{typo}/anular", new { Motivo = "costo mal escrito" }));
        (await Ok(await admin.GetAsync($"/api/inventario/productos/{queso}"))).GetProperty("costo").GetDecimal().ShouldBe(25m);

        var vecino = (await Ok(await admin.PostAsJsonAsync("/api/clientes", new { Nombre = "Colmado Vecino", LimiteCredito = 3000m }))).GetProperty("id").GetGuid();
        var mayorista = (await Ok(await admin.PostAsJsonAsync("/api/clientes", new { Nombre = "Hotel Playa SRL", RncCedula = "130987654", Nivel = "Mayorista", CreditoFiscal = true }))).GetProperty("id").GetGuid();

        // ── Opening: each cashier on their own register ──
        foreach (var caja in cajas)
        {
            caja.TurnoId = (await Ok(await caja.Cliente.PostAsJsonAsync("/api/caja/turnos", new { CajaId = caja.CajaId, FondoInicial = 2000m }))).GetProperty("turnoId").GetGuid();
            caja.Efectivo = 2000m;
        }

        // ── Rush hour: the three registers sell the same products at the same time, in opposite scan orders, with mixed payments and price labels ──
        await Task.WhenAll(cajas.Select((caja, n) => Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                object[] lineas = (i + n) % 2 == 0
                    ? [new { ProductoId = chuleta, Cantidad = 1.35m + i / 10m }, new { ProductoId = refresco, Cantidad = 2m }]
                    : [new { ProductoId = refresco, Cantidad = 1m }, new { ProductoId = arroz, Cantidad = 3m }, new { ProductoId = chuleta, Cantidad = 2.10m }];
                if (i == 3) lineas = [.. lineas, new { ProductoId = chuleta, Cantidad = 1m, CodigoEtiqueta = Etiqueta("1234", 412.50m) }];
                await VenderAsync(caja, lineas, medio: (i % 3) switch { 0 => "Efectivo", 1 => "Tarjeta", _ => "Mixto" });
            }
        })));

        // ── The server restarts in the middle of the day (power cut): sessions and data survive ──
        await servidor.ReiniciarAsync();
        var (c1, c2, c3) = (cajas[0], cajas[1], cajas[2]);

        // Credit fiscal sale with the customer's RNC, and a wholesale sale (the customer is wholesale: no supervisor).
        (await VenderAsync(c1, [new { ProductoId = arroz, Cantidad = 40m }], extra: new() { ["ClienteId"] = mayorista, ["Nivel"] = "Mayorista", ["TipoComprobante"] = "CreditoFiscal31" }))
            .GetProperty("eNcf").GetString()!.ShouldStartWith("E31");

        // A held cart on register 2, resumed on the same register.
        var carrito = Guid.CreateVersion7();
        await Ok(await c2.Cliente.PutAsJsonAsync("/api/ventas/en-espera", new { Id = carrito, TurnoId = c2.TurnoId, Etiqueta = "Señora del pollo", Total = 177m, Contenido = new { lineas = 1 } }));
        (await Ok(await c2.Cliente.GetAsync($"/api/ventas/en-espera?turnoId={c2.TurnoId}"))).GetArrayLength().ShouldBe(1);
        await Ok(await c2.Cliente.DeleteAsync($"/api/ventas/en-espera/{carrito}?turnoId={c2.TurnoId}"));
        await VenderAsync(c2, [new { ProductoId = refresco, Cantidad = 3m }]);

        // A discount over the cap needs the supervisor.
        var conDescuento = Guid.CreateVersion7();
        await VenderAsync(c3, [new { ProductoId = refresco, Cantidad = 4m, Descuento = new { EsPorcentaje = true, Valor = 20m } }], ventaId: conDescuento,
            extra: new() { ["Autorizaciones"] = new[] { await AutorizarAsync(c3, "ventas.descuento-mayor", conDescuento) } });

        // A double-tapped void: one goes through, the other finds it voided.
        var anular = c1.Ventas.First(v => v.EfectivoQueQuedo > 0);
        var anulaciones = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => await c1.Cliente.PostAsJsonAsync($"/api/ventas/{anular.VentaId}/anular",
            new { Motivo = "cliente se arrepintió", AutorizacionSupervisorId = await AutorizarAsync(c1, "ventas.anular", anular.VentaId) })));
        anulaciones.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        c1.Efectivo -= anular.EfectivoQueQuedo;
        c1.Ventas.Remove(anular);

        // A return on register 2's shift of a sale made on register 1 (one refresco back to the shelf, cash).
        var deCaja1 = c1.Ventas.First(v => v.EfectivoQueQuedo >= 60m); // the refund cannot exceed the cash this sale took
        var lineaRefresco = (await Ok(await c2.Cliente.GetAsync($"/api/ventas/{deCaja1.VentaId}"))).GetProperty("lineas").EnumerateArray()
            .First(l => l.GetProperty("descripcion").GetString() == "Refresco 2L").GetProperty("id").GetGuid();
        await DevolverAsync(c2, deCaja1.VentaId, lineaRefresco, 1m, "Efectivo");
        c2.Efectivo -= 59m;

        // The last piece: 3 cheeses for 100.00 by card, returned 1 + 1 + 1 to the card.
        var quesos = Guid.CreateVersion7();
        await VenderAsync(c3, [new { ProductoId = queso, Cantidad = 3m, Descuento = new { EsPorcentaje = false, Valor = 50m } }], medio: "Tarjeta", ventaId: quesos,
            extra: new() { ["Autorizaciones"] = new[] { await AutorizarAsync(c3, "ventas.descuento-mayor", quesos) } });
        var lineaQueso = (await Ok(await c3.Cliente.GetAsync($"/api/ventas/{quesos}"))).GetProperty("lineas")[0].GetProperty("id").GetGuid();
        var devueltos = new List<decimal>();
        for (var i = 0; i < 3; i++) devueltos.Add((await DevolverAsync(c3, quesos, lineaQueso, 1m, "Tarjeta")).GetProperty("total").GetDecimal());
        devueltos.ShouldBe([33.33m, 33.33m, 33.34m]);

        // Credit (fiado): within the limit, a pound returned to the account, then over the limit with the supervisor;
        // payments in cash and by card; a voided credit sale.
        var fiado = Guid.CreateVersion7();
        await VenderAsync(c2, [new { ProductoId = chuleta, Cantidad = 10m }], medio: "Fiado", ventaId: fiado, extra: new() { ["ClienteId"] = vecino });
        var lineaFiado = (await Ok(await c2.Cliente.GetAsync($"/api/ventas/{fiado}"))).GetProperty("lineas")[0].GetProperty("id").GetGuid();
        (await DevolverAsync(c2, fiado, lineaFiado, 1m, "SaldoFavor")).GetProperty("total").GetDecimal().ShouldBe(165m);
        var sobreLimite = Guid.CreateVersion7();
        await VenderAsync(c2, [new { ProductoId = chuleta, Cantidad = 10m }], medio: "Fiado", ventaId: sobreLimite,
            extra: new() { ["ClienteId"] = vecino, ["Autorizaciones"] = new[] { await AutorizarAsync(c2, "fiado.exceder-limite", sobreLimite) } });
        await Ok(await c1.Cliente.PostAsJsonAsync($"/api/clientes/{vecino}/abonos", new { AbonoId = Guid.CreateVersion7(), TurnoId = c1.TurnoId, Monto = 1000m, Medio = "Efectivo" }));
        c1.Efectivo += 1000m;
        await Ok(await c3.Cliente.PostAsJsonAsync($"/api/clientes/{vecino}/abonos", new { AbonoId = Guid.CreateVersion7(), TurnoId = c3.TurnoId, Monto = 300m, Medio = "Tarjeta" }));
        await Ok(await c2.Cliente.PostAsJsonAsync($"/api/ventas/{sobreLimite}/anular",
            new { Motivo = "se llevó menos", AutorizacionSupervisorId = await AutorizarAsync(c2, "ventas.anular", sobreLimite) }));
        c2.Ventas.RemoveAll(v => v.VentaId == sobreLimite);

        // A cash drop and a petty expense, both with the supervisor; a supplier paid by transfer.
        await Ok(await c1.Cliente.PostAsJsonAsync($"/api/caja/turnos/{c1.TurnoId}/movimientos",
            new { TipoMovimiento = "Retiro", Monto = 1500m, Motivo = "Recogida", AutorizacionSupervisorId = await AutorizarAsync(c1, "caja.retirar", c1.TurnoId) }));
        c1.Efectivo -= 1500m;
        await Ok(await c2.Cliente.PostAsJsonAsync($"/api/caja/turnos/{c2.TurnoId}/movimientos",
            new { TipoMovimiento = "Gasto", Monto = 150m, Motivo = "GastoMenor", Nota = "Hielo", AutorizacionSupervisorId = await AutorizarAsync(c2, "caja.retirar", c2.TurnoId) }));
        c2.Efectivo -= 150m;
        await Ok(await admin.PostAsJsonAsync("/api/compras/pagos", new { PagoId = Guid.CreateVersion7(), SuplidorId = suplidor, Monto = 20000m, Medio = "Transferencia", Fecha = hoy }));

        // ── Closing: the supervisor sees the X report; each drawer is counted blind and must match ──
        var cobradoEnCajas = 0m;
        foreach (var caja in cajas)
        {
            var x = await Ok(await admin.GetAsync($"/api/caja/turnos/{caja.TurnoId}/z"));
            x.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(caja.Efectivo, $"{caja.Nombre}: el efectivo esperado no cuadra con lo que pasó por la caja");
            var z = await Ok(await caja.Cliente.PostAsJsonAsync($"/api/caja/turnos/{caja.TurnoId}/cerrar",
                new { Conteo = Contar(caja.Efectivo), AutorizacionSupervisorId = await AutorizarAsync(caja, "caja.aprobar-cierre", caja.TurnoId) }));
            z.GetProperty("diferencia").GetDecimal().ShouldBe(0m);
            z.GetProperty("cadenaAuditoria").GetString()!.ShouldStartWith("#");
            cobradoEnCajas += z.GetProperty("porMedio").EnumerateArray()
                .Sum(m => m.GetProperty("cobros").GetDecimal() - m.GetProperty("anulaciones").GetDecimal() - m.GetProperty("reembolsos").GetDecimal());
        }

        // ── End of day: everything must add up ──
        (await Ok(await admin.GetAsync("/api/auditoria/verificar"))).GetProperty("intacta").GetBoolean().ShouldBeTrue();

        // Kardex: the movements of each product add up to its stock.
        var existencias = (await Ok(await admin.GetAsync("/api/inventario/existencias"))).EnumerateArray().ToDictionary(e => e.GetProperty("productoId").GetGuid(), e => e.GetProperty("cantidad").GetDecimal());
        foreach (var p in productos)
            (await Ok(await admin.GetAsync($"/api/inventario/productos/{p}/movimientos?ultimos=1000"))).EnumerateArray().Sum(m => m.GetProperty("cantidad").GetDecimal()).ShouldBe(existencias[p]);

        // e-NCF: per type, 1..N with no gap and no repeat; each sale document carries the sale's total.
        var documentos = (await Ok(await admin.GetAsync("/api/fiscal/documentos?ultimos=1000"))).EnumerateArray().ToList();
        foreach (var tipo in documentos.GroupBy(d => d.GetProperty("eNcf").GetString()![..3]))
            tipo.Select(d => long.Parse(d.GetProperty("eNcf").GetString()![3..], CultureInfo.InvariantCulture)).Order().ShouldBe(Enumerable.Range(1, tipo.Count()).Select(n => (long)n));
        foreach (var d in documentos.Where(d => !d.GetProperty("eNcf").GetString()!.StartsWith("E34", StringComparison.Ordinal)))
        {
            var venta = await Ok(await admin.GetAsync($"/api/ventas/{d.GetProperty("referenciaId").GetGuid()}"));
            venta.GetProperty("total").GetDecimal().ShouldBe(d.GetProperty("total").GetDecimal());
        }

        // Customer: card debt = statement's last running debt = receivables aging.
        var tarjeta = await Ok(await admin.GetAsync($"/api/clientes/{vecino}"));
        var estado = await Ok(await admin.GetAsync($"/api/clientes/{vecino}/estado-cuenta"));
        var cxc = (await Ok(await admin.GetAsync("/api/reportes/por-cobrar"))).EnumerateArray().Single(c => c.GetProperty("clienteId").GetGuid() == vecino);
        tarjeta.GetProperty("deuda").GetDecimal().ShouldBe(10 * 165m - 165m - 1300m);
        estado.GetProperty("movimientos").EnumerateArray().Last().GetProperty("deuda").GetDecimal().ShouldBe(tarjeta.GetProperty("deuda").GetDecimal());
        cxc.GetProperty("total").GetDecimal().ShouldBe(tarjeta.GetProperty("deuda").GetDecimal());

        // Supplier: statement balance = payables aging.
        var saldoSuplidor = (await Ok(await admin.GetAsync($"/api/compras/suplidores/{suplidor}/estado-cuenta"))).GetProperty("saldo").GetDecimal();
        saldoSuplidor.ShouldBe(300 * 110m - 20000m);
        (await Ok(await admin.GetAsync("/api/reportes/por-pagar"))).EnumerateArray().Single(s => s.GetProperty("suplidorId").GetGuid() == suplidor)
            .GetProperty("total").GetDecimal().ShouldBe(saldoSuplidor);

        // Reports reconcile with the closings, the fiscal documents and the stock. (Tomorrow is included: a run near midnight splits the day.)
        var periodo = $"desde={hoy:yyyy-MM-dd}&hasta={hoy.AddDays(1):yyyy-MM-dd}";
        var dias = (await Ok(await admin.GetAsync($"/api/reportes/ventas/por-dia?{periodo}"))).EnumerateArray().ToList();
        dias.Sum(d => d.GetProperty("ventas").GetInt32()).ShouldBe(cajas.Sum(c => c.Ventas.Count));
        // The drawers took every sale and paid every return except the one credited to the account (165).
        (dias.Sum(d => d.GetProperty("ventasConItbis").GetDecimal() - d.GetProperty("devoluciones").GetDecimal()) + 165m).ShouldBe(cobradoEnCajas);

        var filas607 = (await Ok(await admin.GetAsync($"/api/reportes/dgii/607?{periodo}"))).EnumerateArray().ToList();
        filas607.Count.ShouldBe(documentos.Count);
        var totalPorNcf = documentos.ToDictionary(d => d.GetProperty("eNcf").GetString()!, d => d.GetProperty("total").GetDecimal());
        foreach (var f in filas607)
            (f.GetProperty("montoFacturado").GetDecimal() + f.GetProperty("itbisFacturado").GetDecimal()).ShouldBe(totalPorNcf[f.GetProperty("ncf").GetString()!]);

        foreach (var v in (await Ok(await admin.GetAsync("/api/reportes/inventario/valor"))).EnumerateArray())
            v.GetProperty("existencia").GetDecimal().ShouldBe(existencias[v.GetProperty("productoId").GetGuid()]);

        servidor.Errores500.ShouldBeEmpty(string.Join("\n", servidor.Errores500));

        // ── helpers (local functions keep the day readable top to bottom) ──
        async Task<Guid> AutorizarAsync(Caja caja, string permiso, Guid recurso) =>
            (await Ok(await caja.Cliente.PostAsJsonAsync("/api/identidad/autorizaciones", new { Codigo = "sup", Pin = PinSupervisor, Permiso = permiso, RecursoId = recurso })))
            .GetProperty("autorizacionId").GetGuid();

        async Task<JsonElement> DevolverAsync(Caja caja, Guid ventaId, Guid lineaId, decimal cantidad, string medio) =>
            await Ok(await caja.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/devoluciones", new
            {
                DevolucionId = Guid.CreateVersion7(), TurnoId = caja.TurnoId, Medio = medio, Motivo = "devolución",
                Lineas = new[] { new { LineaVentaId = lineaId, Cantidad = cantidad } }, AutorizacionSupervisorId = await AutorizarAsync(caja, "ventas.devolver", ventaId),
            }));
    }

    /// <summary>Sells like a register: it shows the server's total first (TotalCambio), then charges it. Records the cash the drawer keeps.</summary>
    private static async Task<JsonElement> VenderAsync(Caja caja, object[] lineas, string medio = "Efectivo", Guid? ventaId = null, Dictionary<string, object>? extra = null)
    {
        var id = ventaId ?? Guid.CreateVersion7();
        Dictionary<string, object> Cuerpo(decimal total, object[] pagos)
        {
            var c = new Dictionary<string, object> { ["VentaId"] = id, ["TurnoId"] = caja.TurnoId, ["Lineas"] = lineas, ["Pagos"] = pagos, ["TotalEsperado"] = total };
            foreach (var (k, v) in extra ?? []) c[k] = v;
            return c;
        }

        var tanteo = await ConReintentoAsync(() => caja.Cliente.PostAsJsonAsync("/api/ventas", Cuerpo(-1m, [new { Medio = "Efectivo", Monto = 1m }])));
        var problema = await tanteo.Content.ReadFromJsonAsync<JsonElement>();
        problema.GetProperty("code").GetString().ShouldBe("Ventas.TotalCambio", problema.ToString());
        var total = decimal.Parse(problema.GetProperty("errors").GetProperty("total")[0].GetString()!, CultureInfo.InvariantCulture);

        var efectivo = medio switch { "Efectivo" => Math.Ceiling(total) + (total > 300 ? 100 : 0), "Mixto" => Math.Floor(total / 2), _ => 0m };
        object[] pagos = medio switch
        {
            "Efectivo" => [new { Medio = "Efectivo", Monto = efectivo }],
            "Mixto" => [new { Medio = "Tarjeta", Monto = total - efectivo, Referencia = "AUT" }, new { Medio = "Efectivo", Monto = efectivo }],
            _ => [new { Medio = medio, Monto = total, Referencia = "AUT" }],
        };
        var venta = await Ok(await ConReintentoAsync(() => caja.Cliente.PostAsJsonAsync("/api/ventas", Cuerpo(total, pagos))));
        var queda = efectivo - venta.GetProperty("cambio").GetDecimal();
        lock (caja)
        {
            caja.Efectivo += queda;
            caja.Ventas.Add((id, total, queda));
        }
        return venta;
    }

    /// <summary>A register retries a deadlock victim (409 Ocupado) with the same request: the same VentaId makes the retry safe.</summary>
    private static async Task<HttpResponseMessage> ConReintentoAsync(Func<Task<HttpResponseMessage>> enviar)
    {
        for (var intento = 1; ; intento++)
        {
            var r = await enviar();
            if (r.StatusCode != HttpStatusCode.Conflict || intento == 3) return r;
            var cuerpo = await r.Content.ReadAsStringAsync();
            if (!cuerpo.Contains("\"code\":\"Ocupado\"", StringComparison.Ordinal))
                return new HttpResponseMessage(r.StatusCode) { Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/problem+json"), RequestMessage = r.RequestMessage };
            await Task.Delay(100 * intento);
        }
    }

    private static async Task<string> SesionAsync(ServidorToki servidor, string equipo, string codigo, string pin) =>
        (await Ok(await servidor.Cliente(equipo).PostAsJsonAsync("/api/identidad/sesiones", new { Codigo = codigo, Pin = pin }))).GetProperty("token").GetString()!;

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var texto = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.ShouldBeTrue($"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery} → {(int)r.StatusCode}: {texto}");
        return texto.Length == 0 ? default : JsonDocument.Parse(texto).RootElement.Clone();
    }

    /// <summary>The bills and coins a cashier would count for an amount (greedy, Dominican denominations).</summary>
    private static object[] Contar(decimal monto)
    {
        var conteo = new List<object>();
        foreach (var b in Billetes)
        {
            var n = (int)(monto / b);
            if (n > 0) conteo.Add(new { Denominacion = b, Cantidad = n });
            monto -= n * b;
        }
        monto.ShouldBe(0m, "el efectivo esperado debe ser contable en pesos enteros");
        return [.. conteo];
    }

    /// <summary>A license as the vendor signs it: compact JWS, ES256.</summary>
    private static string LicenciaFirmada(System.Security.Cryptography.ECDsa clave, int cajas)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var encabezado = B64("""{"alg":"ES256","typ":"JWT"}"""u8.ToArray());
        var cuerpo = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Tienda = "Carnicería La Esquina", Plan = "Negocio", Funciones = Array.Empty<string>(), Limites = new Dictionary<string, int> { ["Cajas"] = cajas },
            EmitidaUtc = DateTime.UtcNow, VenceUtc = DateTime.UtcNow.AddDays(30),
        }, JsonSerializerOptions.Web));
        return $"{encabezado}.{cuerpo}.{B64(clave.SignData(System.Text.Encoding.ASCII.GetBytes($"{encabezado}.{cuerpo}"), System.Security.Cryptography.HashAlgorithmName.SHA256))}";
    }

    /// <summary>A scale price label: "22" + 4-digit PLU + amount in centavos (6 digits) + EAN-13 check digit.</summary>
    private static string Etiqueta(string plu, decimal monto)
    {
        var doce = $"22{plu}{(int)(monto * 100):000000}";
        var suma = doce.Select((d, i) => (d - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return doce + (10 - suma % 10) % 10;
    }
}
