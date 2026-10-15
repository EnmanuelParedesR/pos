using System.Net.Http.Json;
using System.Text.Json;

namespace Toki.IntegrationTests.Infra;

/// <summary>A cashier with an open shift on a new register, ready to sell. e-NCF ranges are loaded once per app.</summary>
public sealed record Cajero(Sesion Sesion, HttpClient Cliente, Guid TurnoId, Guid CajaId);

public static class Tienda
{
    private static readonly HashSet<TokiApp> ConSecuencias = [];

    public static async Task CargarSecuenciasAsync(this TokiApp app)
    {
        lock (ConSecuencias) if (!ConSecuencias.Add(app)) return;
        var admin = app.Cliente(await app.AdminAsync());
        foreach (var tipo in new[] { 31, 32, 34 })
            (await admin.PostAsJsonAsync("/api/fiscal/secuencias", new { Tipo = tipo, Desde = 1, Hasta = 100_000, VenceEl = "2030-12-31" })).EnsureSuccessStatusCode();
    }

    public static async Task<Cajero> CajeroConTurnoAsync(this TokiApp app, decimal fondo = 2000m)
    {
        await app.CargarSecuenciasAsync();
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
