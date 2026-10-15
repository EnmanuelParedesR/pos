using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Caja;

public sealed class CajaTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<Guid> CajaAsync(string nombre = "Caja")
    {
        var r = await app.Cliente(await app.AdminAsync()).PostAsJsonAsync("/api/caja/cajas", new { Nombre = nombre + " " + Guid.NewGuid().ToString()[..4] });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    private async Task<(HttpClient Cliente, Guid Turno)> TurnoAsync(decimal fondo = 2000m)
    {
        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        var r = await cajero.PostAsJsonAsync("/api/caja/turnos", new { CajaId = await CajaAsync(), FondoInicial = fondo });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (cajero, (await r.Json()).GetProperty("turnoId").GetGuid());
    }

    private static async Task<Guid> AutorizarAsync(HttpClient c, string permiso, Guid recurso) =>
        (await (await c.PostAsJsonAsync("/api/identidad/autorizaciones", new { Codigo = "admin", Pin = TokiApp.PinAdmin, Permiso = permiso, RecursoId = recurso })).Json())
        .GetProperty("autorizacionId").GetGuid();

    [Fact]
    public async Task Una_caja_no_puede_tener_dos_turnos_y_un_cajero_no_puede_estar_en_dos_cajas()
    {
        var caja = await CajaAsync();
        var ana = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        var luis = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await ana.PostAsJsonAsync("/api/caja/turnos", new { CajaId = caja, FondoInicial = 1000m })).EnsureSuccessStatusCode();

        (await (await luis.PostAsJsonAsync("/api/caja/turnos", new { CajaId = caja, FondoInicial = 1000m })).CodigoError()).ShouldBe("Caja.CajaOcupada");
        (await (await ana.PostAsJsonAsync("/api/caja/turnos", new { CajaId = await CajaAsync(), FondoInicial = 1000m })).CodigoError()).ShouldBe("Caja.CajeroConTurno");

        var actual = await ana.GetFromJsonAsync<JsonElement>("/api/caja/turnos/actual");
        actual.GetProperty("fondoInicial").GetDecimal().ShouldBe(1000m);
    }

    [Fact]
    public async Task Un_retiro_pide_supervisor()
    {
        var (cajero, turno) = await TurnoAsync(fondo: 1000m);

        var sinAutorizacion = await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/movimientos", new { TipoMovimiento = "Retiro", Monto = 500m, Motivo = "Recogida" });
        (await sinAutorizacion.CodigoError()).ShouldBe("Autorizacion.Requerida");

        // No "not enough cash" answer: it would let the cashier probe the expected cash and defeat the blind count.
        (await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/movimientos",
            new { TipoMovimiento = "Retiro", Monto = 600m, Motivo = "Recogida", AutorizacionSupervisorId = await AutorizarAsync(cajero, "caja.retirar", turno) }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/movimientos", new { TipoMovimiento = "Entrada", Monto = 100m, Motivo = "CambioAdicional" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var otro = new { TipoMovimiento = "Gasto", Monto = 10m, Motivo = "Otro" };
        (await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/movimientos", otro)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // "Otro" needs a note
    }

    [Fact]
    public async Task El_cierre_es_ciego_lo_aprueba_un_supervisor_y_da_el_reporte_Z()
    {
        var (cajero, turno) = await TurnoAsync(fondo: 2000m);
        (await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/movimientos", new { TipoMovimiento = "Entrada", Monto = 500m, Motivo = "CambioAdicional" }))
            .EnsureSuccessStatusCode();

        // The cashier cannot see the expected cash while the shift is open (blind count).
        (await cajero.GetAsync($"/api/caja/turnos/{turno}/z")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var x = await app.Cliente(await app.AdminAsync()).GetFromJsonAsync<JsonElement>($"/api/caja/turnos/{turno}/z");
        x.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(2500m);

        var conteo = new[] { new { Denominacion = 1000m, Cantidad = 2 }, new { Denominacion = 200m, Cantidad = 2 }, new { Denominacion = 50m, Cantidad = 1 } };
        (await (await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/cerrar", new { Conteo = conteo })).CodigoError()).ShouldBe("Autorizacion.Requerida");

        var cierre = await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/cerrar",
            new { Conteo = conteo, AutorizacionSupervisorId = await AutorizarAsync(cajero, "caja.aprobar-cierre", turno) });
        cierre.StatusCode.ShouldBe(HttpStatusCode.OK);
        var z = await cierre.Json();
        z.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(2500m);
        z.GetProperty("efectivoContado").GetDecimal().ShouldBe(2450m);
        z.GetProperty("diferencia").GetDecimal().ShouldBe(-50m);
        z.GetProperty("numeroZ").GetInt32().ShouldBe(1);

        // After closing: nothing else can happen on the shift, and the cashier may read their Z.
        (await (await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/movimientos", new { TipoMovimiento = "Entrada", Monto = 1m, Motivo = "CambioAdicional" }))
            .CodigoError()).ShouldBe("Caja.TurnoCerrado");
        (await cajero.GetAsync($"/api/caja/turnos/{turno}/z")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Una_denominacion_que_no_existe_se_rechaza()
    {
        var (cajero, turno) = await TurnoAsync();
        var r = await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/cerrar", new { Conteo = new[] { new { Denominacion = 300m, Cantidad = 1 } } });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task La_caja_queda_ligada_al_equipo_donde_se_abrio()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var codigo = (await (await admin.PostAsJsonAsync("/api/identidad/dispositivos/codigos", new { NombreDispositivo = "Caja 2" })).Json()).GetProperty("codigo").GetString();
        var equipo2 = await (await app.Anonimo().PostAsJsonAsync("/api/identidad/dispositivos/emparejar", new { Codigo = codigo })).Json();
        var usuario = $"u{Guid.NewGuid():N}"[..12];
        (await admin.PostAsJsonAsync("/api/identidad/usuarios", new { Nombre = "Luis", Codigo = usuario, Pin = "4321", RolId = Roles.Cajero })).EnsureSuccessStatusCode();
        var enEquipo2 = app.Cliente(await app.IniciarSesionAsync(usuario, "4321", equipo2.GetProperty("token").GetString()!));

        // The first shift (on the main PC) binds the register; a cashier on another device cannot open it or sell on it.
        var (cajero, turno) = await TurnoAsync();
        var caja = (await cajero.GetFromJsonAsync<JsonElement>("/api/caja/turnos/actual")).GetProperty("cajaId").GetGuid();
        (await (await enEquipo2.PostAsJsonAsync("/api/caja/turnos", new { CajaId = caja, FondoInicial = 0m })).CodigoError()).ShouldBe("Caja.OtroDispositivo");

        // Moving the register to the other device: the old one can no longer operate the shift.
        (await admin.PutAsJsonAsync($"/api/caja/cajas/{caja}/dispositivo", new { DispositivoId = equipo2.GetProperty("dispositivoId").GetGuid() }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await cajero.PostAsJsonAsync($"/api/caja/turnos/{turno}/movimientos", new { TipoMovimiento = "Entrada", Monto = 1m, Motivo = "CambioAdicional" }))
            .CodigoError()).ShouldBe("Caja.OtroDispositivo");
    }

    [Fact]
    public async Task Otro_cajero_no_puede_operar_mi_turno()
    {
        var (_, turno) = await TurnoAsync();
        var otro = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        var r = await otro.PostAsJsonAsync($"/api/caja/turnos/{turno}/movimientos", new { TipoMovimiento = "Entrada", Monto = 1m, Motivo = "CambioAdicional" });
        (await r.CodigoError()).ShouldBe("Caja.TurnoAjeno");
    }
}
