using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Settings;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Configuracion;

/// <summary>TK-092. Own app: it writes backup files.</summary>
public sealed class RespaldosTests(TokiApp app) : IClassFixture<TokiApp>
{
    private const string Frase = "la chuleta de los viernes 2026";

    [Fact]
    public async Task El_cifrado_abre_solo_con_la_frase_y_detecta_un_archivo_cortado()
    {
        var datos = RandomNumberGenerator.GetBytes(2_500_000); // three chunks
        var sal = CifradoRespaldo.NuevaSal();
        using var cifrado = new MemoryStream();
        await CifradoRespaldo.CifrarAsync(new MemoryStream(datos), cifrado, CifradoRespaldo.DerivarClave(Frase, sal), sal, CancellationToken.None);

        using var plano = new MemoryStream();
        await CifradoRespaldo.DescifrarAsync(new MemoryStream(cifrado.ToArray()), plano, Frase, CancellationToken.None);
        plano.ToArray().ShouldBe(datos);

        await Should.ThrowAsync<CryptographicException>(() => CifradoRespaldo.DescifrarAsync(new MemoryStream(cifrado.ToArray()), Stream.Null, "otra frase cualquiera", CancellationToken.None));
        var cortado = cifrado.ToArray()[..^(1024 * 1024)];
        await Should.ThrowAsync<CryptographicException>(() => CifradoRespaldo.DescifrarAsync(new MemoryStream(cortado), Stream.Null, Frase, CancellationToken.None));
    }

    [Fact]
    public async Task Un_respaldo_cifrado_se_restaura_con_la_frase()
    {
        if (SqlServerDePrueba.Activo) Assert.Skip("SQL Server escribe el .bak en su propio equipo (el contenedor); esta prueba usa Sqlite.");
        var admin = app.Cliente(await app.AdminAsync());
        (await (await admin.PostAsync("/api/respaldos", null)).CodigoError()).ShouldBe("Respaldo.SinFrase");
        (await admin.PutAsJsonAsync("/api/respaldos/frase", new { Frase = "corta" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync("/api/respaldos/frase", new { Frase })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PutAsJsonAsync("/api/configuracion/negocio", new { Nombre = "Carnicería del respaldo", TopeDescuentoPct = 10m })).EnsureSuccessStatusCode();

        var hecho = await (await admin.PostAsync("/api/respaldos", null)).Json();
        var lista = await admin.GetFromJsonAsync<JsonElement>("/api/respaldos");
        lista.EnumerateArray().Select(r => r.GetProperty("archivo").GetString()).ShouldContain(hecho.GetProperty("archivo").GetString());

        var carpeta = app.Services.GetRequiredService<IConfiguration>()["Respaldo:Carpeta"]!;
        Directory.GetFiles(carpeta).ShouldAllBe(f => f.EndsWith(Respaldos.Extension)); // no plain copy left behind
        var restaurado = Path.Combine(carpeta, "restaurado.db");
        await using (var entrada = File.OpenRead(Path.Combine(carpeta, hecho.GetProperty("archivo").GetString()!)))
        await using (var salida = File.Create(restaurado))
            await CifradoRespaldo.DescifrarAsync(entrada, salida, Frase, CancellationToken.None);

        await using var conexion = new SqliteConnection($"Data Source={restaurado};Pooling=False");
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT Nombre FROM Negocio";
        (await comando.ExecuteScalarAsync()).ShouldBe("Carnicería del respaldo");
    }
}
