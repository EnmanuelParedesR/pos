using System.Net.Http.Json;
using System.Text.Json;

namespace Toki.IntegrationTests.Infra;

/// <summary>A cashier with an open shift on a new register, ready to sell. e-NCF ranges are loaded once per app.</summary>
public sealed record Cajero(Sesion Sesion, HttpClient Cliente, Guid TurnoId, Guid CajaId);

public static class Tienda
{
    private static readonly HashSet<TokiApp> ConSecuencias = [];

    private static int _ultimoPlu = 1000;

    public static async Task CargarSecuenciasAsync(this TokiApp app)
    {
        lock (ConSecuencias) if (!ConSecuencias.Add(app)) return;
        var admin = app.Cliente(await app.AdminAsync());
        await app.ConfigurarNegocioAsync();
        foreach (var tipo in new[] { 31, 32, 34 })
            (await admin.PostAsJsonAsync("/api/fiscal/secuencias", new { Tipo = tipo, Desde = 1, Hasta = 100_000, VenceEl = "2030-12-31" })).EnsureSuccessStatusCode();
    }

    /// <summary>Issuing needs the shop's RNC; the rest keeps its defaults.</summary>
    public static async Task ConfigurarNegocioAsync(this TokiApp app)
    {
        var admin = app.Cliente(await app.AdminAsync());
        var negocio = await admin.GetFromJsonAsync<JsonElement>("/api/configuracion/negocio");
        (await admin.PutAsJsonAsync("/api/configuracion/negocio", new
        {
            Nombre = "Carnicería de prueba", Rnc = "131245678", TopeDescuentoPct = negocio.GetProperty("topeDescuentoPct").GetDecimal(),
        })).EnsureSuccessStatusCode();
    }

    /// <summary>A PLU no other test uses (PLUs are unique per store).</summary>
    public static string NuevoPlu() => Interlocked.Increment(ref _ultimoPlu).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A price label as the scale prints it: "22" + 4-digit PLU + amount in centavos (6 digits) + EAN-13 check digit.</summary>
    public static string EtiquetaPrecio(string plu, decimal monto)
    {
        var doce = $"22{int.Parse(plu, System.Globalization.CultureInfo.InvariantCulture):0000}{(int)(monto * 100):000000}";
        var suma = doce.Select((d, i) => (d - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return doce + (10 - suma % 10) % 10;
    }

    /// <param name="cargarSecuencias">False for tests that load their own e-NCF ranges.</param>
    public static async Task<Cajero> CajeroConTurnoAsync(this TokiApp app, decimal fondo = 2000m, bool cargarSecuencias = true)
    {
        if (cargarSecuencias) await app.CargarSecuenciasAsync();
        var admin = app.Cliente(await app.AdminAsync());
        var caja = (await (await admin.PostAsJsonAsync("/api/caja/cajas", new { Nombre = "Caja " + Guid.NewGuid().ToString()[..4] })).Json()).GetProperty("id").GetGuid();
        var sesion = await app.UsuarioAsync(Roles.Cajero);
        var cliente = app.Cliente(sesion);
        var turno = await cliente.PostAsJsonAsync("/api/caja/turnos", new { CajaId = caja, FondoInicial = fondo });
        turno.EnsureSuccessStatusCode();
        return new Cajero(sesion, cliente, (await turno.Json()).GetProperty("turnoId").GetGuid(), caja);
    }

    public static async Task<HttpResponseMessage> VenderAsync(this Cajero cajero, object venta) => await cajero.Cliente.PostAsJsonAsync("/api/ventas", venta);

    /// <summary>A minimal sale body: lines (product, quantity), cash payment, expected total.</summary>
    public static object Venta(Guid turnoId, decimal total, decimal efectivo, params object[] lineas) => new
    {
        VentaId = Guid.CreateVersion7(), TurnoId = turnoId, Lineas = lineas, Pagos = new[] { new { Medio = "Efectivo", Monto = efectivo } }, TotalEsperado = total,
    };

    public static object Linea(Guid productoId, decimal cantidad) => new { ProductoId = productoId, Cantidad = cantidad };

    public static async Task<Guid> AutorizarAsync(this HttpClient c, string permiso, Guid recurso)
    {
        var r = await c.PostAsJsonAsync("/api/identidad/autorizaciones", new { Codigo = "admin", Pin = TokiApp.PinAdmin, Permiso = permiso, RecursoId = recurso });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("autorizacionId").GetGuid();
    }

    public static async Task<JsonElement> ReporteXAsync(this TokiApp app, Guid turnoId) =>
        await app.Cliente(await app.AdminAsync()).GetFromJsonAsync<JsonElement>($"/api/caja/turnos/{turnoId}/z");
}
