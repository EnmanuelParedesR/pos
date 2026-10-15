using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Identidad;

public sealed class IdentidadTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Instalar_dos_veces_falla()
    {
        await app.AdminAsync();
        var r = await app.Anonimo().PostAsJsonAsync("/api/identidad/instalacion",
            new { NombreAdministrador = "Otro", CodigoAdministrador = "otro", Pin = "9999", NombreDispositivo = "PC" });
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await r.CodigoError()).ShouldBe("Identidad.YaInstalado");
    }

    [Fact]
    public async Task El_administrador_tiene_todos_los_permisos()
    {
        var admin = await app.AdminAsync();
        var yo = await app.Cliente(admin).GetFromJsonAsync<YoDto>("/api/identidad/yo");
        yo!.Permisos.ShouldContain("usuarios.gestionar");
        yo.Permisos.ShouldContain("ventas.anular");
    }

    [Fact]
    public async Task Sin_dispositivo_no_se_puede_iniciar_sesion()
    {
        await app.AdminAsync();
        var r = await app.Anonimo().PostAsJsonAsync("/api/identidad/sesiones", new { Codigo = "admin", Pin = TokiApp.PinAdmin });
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await r.CodigoError()).ShouldBe("Identidad.DispositivoNoEmparejado");
    }

    [Fact]
    public async Task Cinco_pines_malos_bloquean_aunque_luego_el_pin_sea_correcto()
    {
        var admin = await app.AdminAsync();
        var cajero = await app.UsuarioAsync(Roles.Cajero, pin: "5555");
        var codigo = await CodigoDe(admin, cajero.UsuarioId);

        for (var i = 0; i < 5; i++)
        {
            var mal = await app.Anonimo(admin.Dispositivo).PostAsJsonAsync("/api/identidad/sesiones", new { Codigo = codigo, Pin = "0000" });
            (await mal.CodigoError()).ShouldBe("Identidad.CredencialesInvalidas");
        }

        var bueno = await app.Anonimo(admin.Dispositivo).PostAsJsonAsync("/api/identidad/sesiones", new { Codigo = codigo, Pin = "5555" });
        bueno.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await bueno.CodigoError()).ShouldBe("Identidad.UsuarioBloqueado");
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task El_cajero_no_puede_gestionar_usuarios()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var r = await app.Cliente(cajero).GetAsync("/api/identidad/usuarios");
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Sin_sesion_responde_401()
    {
        var r = await app.Anonimo().GetAsync("/api/identidad/yo");
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Emparejar_una_caja_y_revocarla_cierra_su_sesion()
    {
        var admin = await app.AdminAsync();
        var codigo = await (await app.Cliente(admin).PostAsJsonAsync("/api/identidad/dispositivos/codigos", new { NombreDispositivo = "Caja 2" })).Json();
        var par = await app.Anonimo().PostAsJsonAsync("/api/identidad/dispositivos/emparejar", new { Codigo = codigo.GetProperty("codigo").GetString() });
        par.StatusCode.ShouldBe(HttpStatusCode.OK);
        var caja2 = await par.Json();
        var tokenCaja2 = caja2.GetProperty("token").GetString()!;

        var sesion = await app.IniciarSesionAsync("admin", TokiApp.PinAdmin, tokenCaja2);
        (await app.Cliente(sesion).GetAsync("/api/identidad/yo")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The same code cannot pair twice.
        var otra = await app.Anonimo().PostAsJsonAsync("/api/identidad/dispositivos/emparejar", new { Codigo = codigo.GetProperty("codigo").GetString() });
        otra.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var revocar = await app.Cliente(admin).PostAsync($"/api/identidad/dispositivos/{caja2.GetProperty("dispositivoId").GetGuid()}/revocar", null);
        revocar.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.Cliente(sesion).GetAsync("/api/identidad/yo")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Un_cajero_no_puede_autorizar_como_supervisor()
    {
        var admin = await app.AdminAsync();
        var cajero = await app.UsuarioAsync(Roles.Cajero, pin: "7777");
        var codigo = await CodigoDe(admin, cajero.UsuarioId);
        var r = await app.Cliente(cajero).PostAsJsonAsync("/api/identidad/autorizaciones",
            new { Codigo = codigo, Pin = "7777", Permiso = "ventas.anular", RecursoId = Guid.NewGuid() });
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await r.CodigoError()).ShouldBe("Identidad.SupervisorSinPermiso");
    }

    [Fact]
    public async Task Un_supervisor_autoriza_con_su_pin()
    {
        var cajero = await app.UsuarioAsync(Roles.Cajero);
        var r = await app.Cliente(cajero).PostAsJsonAsync("/api/identidad/autorizaciones",
            new { Codigo = "admin", Pin = TokiApp.PinAdmin, Permiso = "ventas.anular", RecursoId = Guid.NewGuid() });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Json()).GetProperty("autorizacionId").GetGuid().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task El_rol_administrador_no_se_puede_editar_y_permisos_desconocidos_se_rechazan()
    {
        var admin = await app.AdminAsync();
        var c = app.Cliente(admin);
        (await c.PutAsJsonAsync($"/api/identidad/roles/{Roles.Administrador}", new { Nombre = "X", Permisos = Array.Empty<string>() }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await c.PostAsJsonAsync("/api/identidad/roles", new { Nombre = "Raro", Permisos = new[] { "no.existe" } }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await c.PostAsJsonAsync("/api/identidad/roles", new { Nombre = "Inventarista", Permisos = new[] { "inventario.contar" } }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private async Task<string> CodigoDe(Sesion admin, Guid usuarioId)
    {
        var usuarios = await app.Cliente(admin).GetFromJsonAsync<List<UsuarioDto>>("/api/identidad/usuarios");
        return usuarios!.Single(u => u.Id == usuarioId).Codigo;
    }

    private sealed record YoDto(Guid UsuarioId, string Nombre, List<string> Permisos);
    private sealed record UsuarioDto(Guid Id, string Codigo);
}
