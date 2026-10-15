using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Toki.Client.UI;
using Toki.Client.UI.Api;
using Toki.Client.UI.Estado;

namespace Toki.Client.UI.Tests;

/// <summary>The local API as the screens see it: each test answers the routes it cares about and reads what was sent.</summary>
internal sealed class ApiFalsa : HttpMessageHandler
{
    private readonly List<(string Metodo, string Ruta, Func<string, HttpResponseMessage> Responder)> _rutas = [];
    public List<(string Metodo, string Ruta, string Cuerpo)> Pedidos { get; } = [];

    public ApiFalsa Responder(string metodo, string ruta, Func<string, HttpResponseMessage> responder)
    {
        _rutas.Insert(0, (metodo, ruta, responder)); // the latest wins, so a test can change an answer midway
        return this;
    }

    public ApiFalsa Json(string metodo, string ruta, object cuerpo, HttpStatusCode estado = HttpStatusCode.OK) =>
        Responder(metodo, ruta, _ => Respuesta(estado, cuerpo));

    public static HttpResponseMessage Respuesta(HttpStatusCode estado, object cuerpo) =>
        new(estado) { Content = new StringContent(JsonSerializer.Serialize(cuerpo, TokiApi.Json), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Problema(HttpStatusCode estado, string codigo, string detalle, object? errores = null) =>
        Respuesta(estado, new { status = (int)estado, code = codigo, detail = detalle, errors = errores });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ruta = request.RequestUri!.PathAndQuery;
        var cuerpo = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Pedidos.Add((request.Method.Method, ruta, cuerpo));
        foreach (var r in _rutas)
            if (r.Metodo == request.Method.Method && (ruta == r.Ruta || ruta.StartsWith(r.Ruta + "?", StringComparison.Ordinal)))
                return r.Responder(cuerpo);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

internal sealed class AlmacenEnMemoria : IAlmacenLocal
{
    private readonly Dictionary<string, string> _datos = [];
    public Task<string?> LeerAsync(string clave) => Task.FromResult(_datos.GetValueOrDefault(clave));
    public Task GuardarAsync(string clave, string? valor)
    {
        if (valor is null) _datos.Remove(clave);
        else _datos[clave] = valor;
        return Task.CompletedTask;
    }
}

internal static class Contexto
{
    public static readonly TurnoActual Turno = new(Guid.NewGuid(), Guid.NewGuid(), "Caja 1", 500m, DateTime.UtcNow);

    /// <summary>A bUnit context with the register's services on a fake API, a paired device and a logged-in cashier.</summary>
    public static (BunitContext Ctx, ApiFalsa Api) Crear(params string[] permisos)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new ApiFalsa();
        ctx.Services.AddTokiCliente(new Uri("http://toki.test"), api);
        ctx.Services.AddSingleton<IAlmacenLocal>(new AlmacenEnMemoria());
        var sesion = ctx.Services.GetRequiredService<Sesion>();
        sesion.EmparejarAsync("dispositivo").GetAwaiter().GetResult();
        sesion.Abrir(new SesionAbierta("token", Guid.NewGuid(), "Yudelka M.", DateTime.UtcNow.AddHours(12)), new Yo(Guid.NewGuid(), "Yudelka M.", Guid.NewGuid(), permisos));
        sesion.Turno = Turno;
        return (ctx, api);
    }
}
