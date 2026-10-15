using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using Toki.Fiscal.Features;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>DGII RNC registry (TK-053). Own app: the import replaces the whole table.</summary>
public sealed class PadronTests(TokiApp app) : IClassFixture<TokiApp>
{
    private const string Archivo =
        "RNC|RAZÓN SOCIAL|NOMBRE COMERCIAL|ACTIVIDAD|||||FECHA|ESTADO|REGIMEN\r\n" +
        "131245678|COLMADO EL SOL SRL|EL SOL|VENTA AL POR MENOR|||||01/02/2010|ACTIVO|NORMAL\r\n" +
        "00112345678|JUAN PÉREZ||||||||SUSPENDIDO|\r\n" +
        "131245678|DUPLICADO|||||||||\r\n" +
        "123|MAL||||||||ACTIVO|\r\n";

    [Fact]
    public void Una_linea_del_padron_se_lee_y_las_malas_se_saltan()
    {
        var c = Padron.Leer("131-24567-8|COLMADO EL SOL SRL|EL SOL|x|||||f|ACTIVO|NORMAL")!;
        (c.Rnc, c.Nombre, c.NombreComercial, c.Activo).ShouldBe(("131245678", "COLMADO EL SOL SRL", "EL SOL", true));
        Padron.Leer("RNC|RAZÓN SOCIAL").ShouldBeNull();
        Padron.Leer("").ShouldBeNull();
        Padron.Leer("131245678|").ShouldBeNull();
    }

    [Fact]
    public async Task Importar_el_padron_reemplaza_el_anterior_y_se_consulta_por_RNC_o_cedula()
    {
        var admin = app.Cliente(await app.AdminAsync());
        async Task<int> Importar(string texto)
        {
            var r = await admin.PostAsync("/api/fiscal/padron", new ByteArrayContent(Encoding.Latin1.GetBytes(texto)));
            r.EnsureSuccessStatusCode();
            return (await r.Json()).GetProperty("contribuyentes").GetInt32();
        }

        (await Importar(Archivo)).ShouldBe(2);
        var sol = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/contribuyentes/131-24567-8");
        sol.GetProperty("nombre").GetString().ShouldBe("COLMADO EL SOL SRL");
        var juan = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/contribuyentes/001-1234567-8");
        juan.GetProperty("nombre").GetString().ShouldBe("JUAN PÉREZ"); // ANSI accents survive
        juan.GetProperty("activo").GetBoolean().ShouldBeFalse();

        // A UTF-8 file is detected and read as such (accents intact either way).
        var utf8 = await admin.PostAsync("/api/fiscal/padron", new ByteArrayContent(Encoding.UTF8.GetBytes("40212345678|ANA LÓPEZ||||||||ACTIVO|\r\n")));
        (await utf8.Json()).GetProperty("contribuyentes").GetInt32().ShouldBe(1);
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/contribuyentes/40212345678")).GetProperty("nombre").GetString().ShouldBe("ANA LÓPEZ");
        (await admin.GetAsync("/api/fiscal/contribuyentes/131245678")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await cajero.GetAsync("/api/fiscal/contribuyentes/40212345678")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cajero.PostAsync("/api/fiscal/padron", new StringContent(Archivo))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
