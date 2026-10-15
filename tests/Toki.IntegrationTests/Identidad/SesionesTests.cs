using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.Identity.Persistence;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Identidad;

/// <summary>Session lifecycle and user rules. Own app because one test moves the clock 12 hours.</summary>
public sealed class SesionesTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<string> CodigoDe(Guid usuarioId)
    {
        var usuarios = await app.Cliente(await app.AdminAsync()).GetFromJsonAsync<List<JsonElement>>("/api/identidad/usuarios");
        return usuarios!.Single(u => u.GetProperty("id").GetGuid() == usuarioId).GetProperty("codigo").GetString()!;
    }

    private static async Task<HttpStatusCode> Yo(HttpClient c) => (await c.GetAsync("/api/identidad/yo")).StatusCode;

    [Fact]
    public async Task Cerrar_sesion_la_termina()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var c = app.Cliente(cajero);
        (await c.DeleteAsync("/api/identidad/sesiones/actual")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Yo(c)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Desactivar_un_usuario_corta_su_sesion_de_inmediato()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        (await admin.PutAsJsonAsync($"/api/identidad/usuarios/{cajero.UsuarioId}", new { Nombre = "Ex cajero", RolId = Roles.Cajero, Activo = false }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Yo(app.Cliente(cajero))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task El_administrador_no_puede_desactivarse_a_si_mismo()
    {
        var admin = await app.AdminAsync();
        var r = await app.Cliente(admin).PutAsJsonAsync($"/api/identidad/usuarios/{admin.UsuarioId}", new { Nombre = "Dueña", RolId = Roles.Administrador, Activo = false });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Un_supervisor_no_puede_dar_el_rol_administrador()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var rol = await (await admin.PostAsJsonAsync("/api/identidad/roles", new { Nombre = "Encargado", Permisos = new[] { "usuarios.gestionar" } })).Json();
        var encargado = app.Cliente(await app.UsuarioAsync(rol.GetProperty("id").GetGuid()));
        var r = await encargado.PostAsJsonAsync("/api/identidad/usuarios", new { Nombre = "Nuevo jefe", Codigo = "jefe", Pin = "1111", RolId = Roles.Administrador });
        (await r.CodigoError()).ShouldBe("Identidad.SoloAdministrador");
    }

    [Fact]
    public async Task Cambiar_el_rol_tiene_efecto_en_la_siguiente_peticion()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var c = app.Cliente(cajero);
        (await c.GetAsync("/api/inventario/existencias")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var rol = await (await admin.PostAsJsonAsync("/api/identidad/roles", new { Nombre = "Solo vende", Permisos = new[] { "ventas.registrar" } })).Json();
        (await admin.PutAsJsonAsync($"/api/identidad/usuarios/{cajero.UsuarioId}", new { Nombre = "Cajero", RolId = rol.GetProperty("id").GetGuid(), Activo = true }))
            .EnsureSuccessStatusCode();
        (await c.GetAsync("/api/inventario/existencias")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cambiar_mi_pin_pide_el_actual_y_cierra_mis_otras_sesiones()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero, pin: "2468");
        var otra = await app.IniciarSesionAsync(await CodigoDe(cajero.UsuarioId), "2468", cajero.Dispositivo);
        var c = app.Cliente(cajero);

        var mal = await c.PostAsJsonAsync("/api/identidad/yo/pin", new { PinActual = "0000", PinNuevo = "1357" });
        mal.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await c.PostAsJsonAsync("/api/identidad/yo/pin", new { PinActual = "2468", PinNuevo = "1357" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Yo(c)).ShouldBe(HttpStatusCode.OK);
        (await Yo(app.Cliente(otra))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task El_codigo_no_distingue_mayusculas()
    {
        var admin = await app.AdminAsync();
        (await app.IniciarSesionAsync("ADMIN", TokiApp.PinAdmin, admin.Dispositivo)).Token.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Pines_malos_en_paralelo_no_se_pierden_y_la_auditoria_sigue_intacta()
    {
        var admin = await app.AdminAsync();
        var cajero = await app.UsuarioAsync(Roles.Cajero, pin: "8642");
        var codigo = await CodigoDe(cajero.UsuarioId);

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            app.Anonimo(admin.Dispositivo).PostAsJsonAsync("/api/identidad/sesiones", new { Codigo = codigo, Pin = "0000" })));

        respuestas.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Unauthorized || r.StatusCode == HttpStatusCode.Conflict);
        var contados = 0;
        foreach (var r in respuestas.Where(r => r.StatusCode == HttpStatusCode.Unauthorized))
            if (await r.CodigoError() == "Identidad.CredencialesInvalidas") contados++;

        var intentos = await app.EnScopeAsync(sp => sp.GetRequiredService<IdentidadDbContext>().Usuarios
            .Where(u => u.Id == cajero.UsuarioId).Select(u => u.IntentosFallidos).SingleAsync());
        intentos.ShouldBe(contados); // every reported failure was counted: no lost updates
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
        var secuencias = await app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Auditoria.Select(a => a.Secuencia).OrderBy(s => s).ToListAsync());
        secuencias.ShouldBe(Enumerable.Range(1, secuencias.Count).Select(i => (long)i));
    }

    [Fact]
    public async Task Un_codigo_de_emparejamiento_en_paralelo_solo_empareja_una_caja()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var codigo = (await (await admin.PostAsJsonAsync("/api/identidad/dispositivos/codigos", new { NombreDispositivo = "Caja X" })).Json()).GetProperty("codigo").GetString();
        var respuestas = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => app.Anonimo().PostAsJsonAsync("/api/identidad/dispositivos/emparejar", new { Codigo = codigo })));
        respuestas.Count(r => r.IsSuccessStatusCode).ShouldBe(1);
    }

    [Fact]
    public async Task Emparejar_sin_codigo_es_400() =>
        (await app.Anonimo().PostAsJsonAsync("/api/identidad/dispositivos/emparejar", new { })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

}

/// <summary>Own app: it moves the clock 12 hours.</summary>
public sealed class VencimientoSesionTests(TokiApp app) : IClassFixture<TokiApp>
{
    private static async Task<HttpStatusCode> Yo(HttpClient c) => (await c.GetAsync("/api/identidad/yo")).StatusCode;

    [Fact]
    public async Task La_sesion_vence_a_las_12_horas()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var c = app.Cliente(cajero);
        app.Reloj.Advance(TimeSpan.FromHours(11));
        (await Yo(c)).ShouldBe(HttpStatusCode.OK);
        app.Reloj.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        (await Yo(c)).ShouldBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>Install runs once even when two requests race on a new database.</summary>
public sealed class InstalacionTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Dos_instalaciones_a_la_vez_crean_un_solo_administrador()
    {
        var respuestas = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => app.Anonimo().PostAsJsonAsync("/api/identidad/instalacion",
            new { NombreAdministrador = "Dueño " + i, CodigoAdministrador = "a" + i, Pin = "1234", NombreDispositivo = "PC" })));
        respuestas.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
    }
}
