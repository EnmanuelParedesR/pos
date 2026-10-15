using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;
using Toki.Settings;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Configuracion;

/// <summary>TK-089. Own app: it installs licenses and moves the clock.</summary>
public sealed class LicenciaTests(TokiApp app) : IClassFixture<TokiApp>
{
    private DatosLicencia Licencia(int cajas, DateTime vence) =>
        new("Carnicería La Esquina", "Negocio", ["ModuloCarniceria"], new Dictionary<string, int> { ["Cajas"] = cajas }, app.Reloj.GetUtcNow().UtcDateTime, vence);

    [Fact]
    public void Solo_vale_una_licencia_firmada_con_la_clave_de_Toki()
    {
        using var otra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jws = Jws.Firmar(Licencia(3, DateTime.UtcNow.AddDays(30)), app.ClaveLicencias);
        Jws.Verificar(jws, app.ClaveLicencias).Value.Limites["Cajas"].ShouldBe(3);
        Jws.Verificar(jws, otra).IsFailure.ShouldBeTrue();
        // Changing the body (more registers) breaks the signature.
        var partes = jws.Split('.');
        var cuerpo = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Licencia(99, DateTime.UtcNow.AddDays(30)), JsonSerializerOptions.Web)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Jws.Verificar($"{partes[0]}.{cuerpo}.{partes[2]}", app.ClaveLicencias).IsFailure.ShouldBeTrue();
        Jws.Verificar("no.es.licencia", app.ClaveLicencias).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task La_licencia_fija_cuantas_cajas_se_emparejan_y_vence_con_gracia()
    {
        var admin = app.Cliente(await app.AdminAsync());
        async Task<HttpResponseMessage> Emparejar()
        {
            var codigo = (await (await admin.PostAsJsonAsync("/api/identidad/dispositivos/codigos", new { NombreDispositivo = "Caja" })).Json()).GetProperty("codigo").GetString();
            return await app.Anonimo().PostAsJsonAsync("/api/identidad/dispositivos/emparejar", new { Codigo = codigo });
        }
        async Task<JsonElement> Funciones() => await admin.GetFromJsonAsync<JsonElement>("/api/configuracion/funciones");

        // Without a license: trial with two registers.
        (await Funciones()).GetProperty("estado").GetString().ShouldBe("Prueba");
        (await Emparejar()).EnsureSuccessStatusCode();
        (await Emparejar()).EnsureSuccessStatusCode();
        (await (await Emparejar()).CodigoError()).ShouldBe("Licencia.LimiteCajas");

        // A forged license is refused; the vendor's one opens a third register.
        using var otra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (await (await admin.PutAsJsonAsync("/api/configuracion/licencia", new { Licencia = Jws.Firmar(Licencia(10, DateTime.UtcNow.AddDays(30)), otra) })).CodigoError())
            .ShouldBe("Licencia.Invalida");
        var vence = app.Reloj.GetUtcNow().UtcDateTime.AddDays(30);
        (await admin.PutAsJsonAsync("/api/configuracion/licencia", new { Licencia = Jws.Firmar(Licencia(3, vence), app.ClaveLicencias) })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var funciones = await Funciones();
        (funciones.GetProperty("estado").GetString(), funciones.GetProperty("cajas").GetInt32(), funciones.GetProperty("plan").GetString()).ShouldBe(("Activa", 3, "Negocio"));
        funciones.GetProperty("activas").EnumerateArray().Select(f => f.GetString()).ShouldBe(["ModuloCarniceria"]);
        (await Emparejar()).EnsureSuccessStatusCode();
        (await (await Emparejar()).CodigoError()).ShouldBe("Licencia.LimiteCajas");

        // Expired: 15 days of grace keep everything; after that, paid functions go off (selling never does).
        app.Reloj.Advance(TimeSpan.FromDays(31));
        admin = app.Cliente(await app.IniciarSesionAsync("admin", TokiApp.PinAdmin, (await app.AdminAsync()).Dispositivo));
        (await Funciones()).GetProperty("estado").GetString().ShouldBe("Gracia");
        app.Reloj.Advance(ServicioLicencia.Gracia);
        admin = app.Cliente(await app.IniciarSesionAsync("admin", TokiApp.PinAdmin, (await app.AdminAsync()).Dispositivo));
        funciones = await Funciones();
        (funciones.GetProperty("estado").GetString(), funciones.GetProperty("cajas").GetInt32()).ShouldBe(("Restringida", ServicioLicencia.CajasDePrueba));
        funciones.GetProperty("activas").GetArrayLength().ShouldBe(0);
    }
}
