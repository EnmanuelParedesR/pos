using System.Net.Http.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Identidad;

/// <summary>TK-014: a supervisor authorization is single-use and bound to its action, resource, device and two minutes.
/// Own app because one test moves the clock.</summary>
public sealed class AutorizacionSupervisorTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<Guid> AutorizarAsync(Sesion cajero, Guid recurso, string permiso = "ventas.anular")
    {
        var r = await app.Cliente(cajero).PostAsJsonAsync("/api/identidad/autorizaciones",
            new { Codigo = "admin", Pin = TokiApp.PinAdmin, Permiso = permiso, RecursoId = recurso });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("autorizacionId").GetGuid();
    }

    [Fact]
    public async Task Sin_autorizacion_se_pide_supervisor()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var r = await app.EjecutarAsync(cajero, new AccionSupervisada(Guid.NewGuid(), null));
        r.Error!.Code.ShouldBe("Autorizacion.Requerida");
    }

    [Fact]
    public async Task Se_usa_una_sola_vez_y_queda_en_la_auditoria()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var recurso = Guid.NewGuid();
        var id = await AutorizarAsync(cajero, recurso);

        (await app.EjecutarAsync(cajero, new AccionSupervisada(recurso, id))).IsSuccess.ShouldBeTrue();
        (await app.EjecutarAsync(cajero, new AccionSupervisada(recurso, id))).Error!.Code.ShouldBe("Identidad.AutorizacionInvalida");
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task No_sirve_para_otro_recurso_ni_otra_accion()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var recurso = Guid.NewGuid();
        var id = await AutorizarAsync(cajero, recurso, permiso: "caja.retirar");

        (await app.EjecutarAsync(cajero, new AccionSupervisada(recurso, id))).Error!.Code.ShouldBe("Identidad.AutorizacionInvalida");
        var otra = await AutorizarAsync(cajero, recurso);
        (await app.EjecutarAsync(cajero, new AccionSupervisada(Guid.NewGuid(), otra))).Error!.Code.ShouldBe("Identidad.AutorizacionInvalida");
    }

    [Fact]
    public async Task No_sirve_desde_otro_dispositivo()
    {
        var admin = await app.AdminAsync();
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var recurso = Guid.NewGuid();
        var id = await AutorizarAsync(cajero, recurso);

        var codigo = await (await app.Cliente(admin).PostAsJsonAsync("/api/identidad/dispositivos/codigos", new { NombreDispositivo = "Caja 3" })).Json();
        var caja3 = await (await app.Anonimo().PostAsJsonAsync("/api/identidad/dispositivos/emparejar", new { Codigo = codigo.GetProperty("codigo").GetString() })).Json();
        var usuarios = await app.Cliente(admin).GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/identidad/usuarios");
        var codigoCajero = usuarios!.Single(u => u.GetProperty("id").GetGuid() == cajero.UsuarioId).GetProperty("codigo").GetString()!;
        var enCaja3 = await app.IniciarSesionAsync(codigoCajero, "4321", caja3.GetProperty("token").GetString()!);

        (await app.EjecutarAsync(enCaja3, new AccionSupervisada(recurso, id))).Error!.Code.ShouldBe("Identidad.AutorizacionInvalida");
    }

    [Fact]
    public async Task Vence_a_los_dos_minutos()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var recurso = Guid.NewGuid();
        var id = await AutorizarAsync(cajero, recurso);

        app.Reloj.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        (await app.EjecutarAsync(cajero, new AccionSupervisada(recurso, id))).Error!.Code.ShouldBe("Identidad.AutorizacionInvalida");
    }
}
