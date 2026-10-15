using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.Settings;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Configuracion;

/// <summary>R6: how backups behave on real store PCs (off at night, full USB sticks, a lost key ring). Own app: it moves the clock.</summary>
public sealed class RespaldosOperacionTests(TokiApp app) : IClassFixture<TokiApp>
{
    private const string Frase = "la chuleta de los viernes 2026";

    [Fact]
    public async Task El_formato_autentica_el_encabezado_y_cada_archivo_usa_su_propia_clave()
    {
        var datos = RandomNumberGenerator.GetBytes(10_000);
        var sal = CifradoRespaldo.NuevaSal();
        var clave = CifradoRespaldo.DerivarClave(Frase, sal);
        async Task<byte[]> Cifrar()
        {
            using var salida = new MemoryStream();
            await CifradoRespaldo.CifrarAsync(new MemoryStream(datos), salida, clave, sal, CancellationToken.None);
            return salida.ToArray();
        }
        var a = await Cifrar();
        (await Cifrar()).ShouldNotBe(a); // same data and phrase, different file key

        foreach (var posicion in new[] { 30, 40 }) // the random bytes of the header: not part of the key derivation input before, now authenticated
        {
            var cambiado = a.ToArray();
            cambiado[posicion] ^= 1;
            await Should.ThrowAsync<CryptographicException>(() => CifradoRespaldo.DescifrarAsync(new MemoryStream(cambiado), Stream.Null, Frase, CancellationToken.None));
        }
        var iteraciones = a.ToArray();
        iteraciones[24] = 0x7F; // ~2 billion iterations would take hours: refused at once
        await Should.ThrowAsync<CryptographicException>(() => CifradoRespaldo.DescifrarAsync(new MemoryStream(iteraciones), Stream.Null, Frase, CancellationToken.None));
    }

    [Fact]
    public async Task Al_arrancar_respalda_si_hace_falta_la_copia_fallida_no_frena_nada_y_una_clave_ilegible_se_explica()
    {
        if (!SqlServerDePrueba.RespaldaEnEsteEquipo) Assert.Skip("SQL Server escribe el .bak dentro del contenedor; use Sqlite o TOKI_TEST_SQLSERVER.");
        var admin = app.Cliente(await app.AdminAsync());
        (await admin.PutAsJsonAsync("/api/respaldos/frase", new { Frase })).EnsureSuccessStatusCode();

        // A PC that is off at 23:30: the backup happens after it starts, once.
        var trabajador = app.Services.GetServices<IHostedService>().OfType<RespaldoNocturno>().Single();
        await trabajador.RespaldarAsync(soloSiHaceFalta: true, CancellationToken.None);
        app.Reloj.Advance(TimeSpan.FromMinutes(1));
        await trabajador.RespaldarAsync(soloSiHaceFalta: true, CancellationToken.None);
        var carpeta = app.Services.GetRequiredService<IConfiguration>()["Respaldo:Carpeta"]!;
        Directory.GetFiles(carpeta, "*" + Respaldos.Extension).Length.ShouldBe(1);
        Directory.GetFiles(Path.Combine(carpeta, "trabajo")).ShouldBeEmpty(); // no plain copy left

        // The off-PC copy fails (here: its path is a file): the local backups go on and keep their retention.
        var noEsCarpeta = Path.GetTempFileName();
        var otra = SqlServerDePrueba.CarpetaRespaldos();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Respaldo:Carpeta"] = otra, ["Respaldo:CopiaEn"] = noEsCarpeta, ["Respaldo:Conservar"] = "2",
        }).Build();
        var estado = new EstadoRespaldo();
        for (var i = 0; i < 3; i++)
        {
            app.Reloj.Advance(TimeSpan.FromMinutes(1));
            var r = await app.EnScopeAsync(sp => new Respaldos(sp.GetRequiredService<ConfiguracionDbContext>(), sp.GetRequiredService<DatabaseOptions>(),
                sp.GetRequiredService<IDataProtectionProvider>(), config, app.Reloj, estado, NullLogger<Respaldos>.Instance).CrearAsync(CancellationToken.None));
            r.IsSuccess.ShouldBeTrue();
        }
        Directory.GetFiles(otra, "*" + Respaldos.Extension).Length.ShouldBe(2);
        estado.ErrorCopia!.ShouldContain(noEsCarpeta);

        // The key ring of this PC cannot open the stored key (restored on a new PC): a clear message, not a 500.
        await app.EnScopeAsync(sp => sp.GetRequiredService<ConfiguracionDbContext>().Negocio.ExecuteUpdateAsync(s => s.SetProperty(n => n.RespaldoClave, "AAAA")));
        (await (await admin.PostAsync("/api/respaldos", null)).CodigoError()).ShouldBe("Respaldo.FraseIlegible");
        (await (await app.Anonimo().GetAsync("/health")).Content.ReadAsStringAsync()).ShouldBe("Degraded");
        (await admin.PutAsJsonAsync("/api/respaldos/frase", new { Frase })).StatusCode.ShouldBe(HttpStatusCode.NoContent); // typing it again fixes it
        app.Reloj.Advance(TimeSpan.FromMinutes(1));
        (await admin.PostAsync("/api/respaldos", null)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Un_respaldo_cancelado_no_deja_archivos()
    {
        var admin = app.Cliente(await app.AdminAsync());
        (await admin.PutAsJsonAsync("/api/respaldos/frase", new { Frase })).EnsureSuccessStatusCode();
        var carpeta = app.Services.GetRequiredService<IConfiguration>()["Respaldo:Carpeta"]!;
        Directory.CreateDirectory(Path.Combine(carpeta, "trabajo"));
        var antes = Directory.GetFiles(carpeta).Length;
        using var cancelado = new CancellationTokenSource();
        await cancelado.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            app.EnScopeAsync(sp => sp.GetRequiredService<Respaldos>().CrearAsync(cancelado.Token)));
        Directory.GetFiles(carpeta).Length.ShouldBe(antes);
        Directory.GetFiles(carpeta, "*.parcial").ShouldBeEmpty();
        Directory.GetFiles(Path.Combine(carpeta, "trabajo")).ShouldBeEmpty();
    }
}
