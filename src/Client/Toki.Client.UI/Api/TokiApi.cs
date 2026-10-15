using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toki.Client.UI.Estado;

namespace Toki.Client.UI.Api;

/// <summary>What the API said when it refused: its ProblemDetails <c>code</c>, the text for the cashier and the field errors.</summary>
public sealed record Problema(int Estado, string Codigo, string Mensaje, IReadOnlyDictionary<string, string[]>? Errores = null)
{
    public const string SinServidor = "SinServidor";

    /// <summary>The permiso a supervisor must grant, from "...supervisor (ventas.anular)." (the API has no field for it).</summary>
    public string? PermisoRequerido => Codigo == "Autorizacion.Requerida" && Mensaje.LastIndexOf('(') is var i and >= 0 && Mensaje.IndexOf(')', i) is var j and > 0
        ? Mensaje[(i + 1)..j]
        : null;

    public string? Error(string campo) => Errores?.GetValueOrDefault(campo)?.FirstOrDefault();
}

public readonly record struct Respuesta<T>(T? Valor, Problema? Problema)
{
    public bool Ok => Problema is null;
}

/// <summary>Typed calls to the store's local API. Every request carries this PC's device token and, once someone logged in, their session.</summary>
public sealed class TokiApi(HttpClient http, Sesion sesion)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<Respuesta<T>> GetAsync<T>(string url) => EnviarAsync<T>(HttpMethod.Get, url, null);
    public Task<Respuesta<T>> PostAsync<T>(string url, object? cuerpo = null) => EnviarAsync<T>(HttpMethod.Post, url, cuerpo);
    public Task<Respuesta<T>> PutAsync<T>(string url, object? cuerpo) => EnviarAsync<T>(HttpMethod.Put, url, cuerpo);
    public Task<Respuesta<T>> DeleteAsync<T>(string url) => EnviarAsync<T>(HttpMethod.Delete, url, null);

    private async Task<Respuesta<T>> EnviarAsync<T>(HttpMethod metodo, string url, object? cuerpo)
    {
        using var pedido = new HttpRequestMessage(metodo, url);
        if (sesion.TokenDispositivo is { } dispositivo) pedido.Headers.Add("X-Dispositivo", dispositivo);
        if (sesion.Token is { } token) pedido.Headers.Authorization = new("Bearer", token);
        if (cuerpo is not null) pedido.Content = JsonContent.Create(cuerpo, cuerpo.GetType(), options: Json);

        HttpResponseMessage r;
        try
        {
            r = await http.SendAsync(pedido);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new(default, new Problema(0, Problema.SinServidor, "No hay conexión con el servidor de la tienda. Revise que la PC principal esté encendida."));
        }

        using (r)
        {
            if (r.IsSuccessStatusCode)
            {
                if (r.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(Vacio) || r.Content.Headers.ContentLength == 0) return new(default, null);
                return new(await r.Content.ReadFromJsonAsync<T>(Json), null);
            }

            var problema = await LeerProblemaAsync(r);
            // A session that expired (12 h) or was closed elsewhere: back to the PIN screen. A wrong PIN has its own code.
            if (r.StatusCode == HttpStatusCode.Unauthorized && problema.Codigo == "" && sesion.Token is not null) sesion.Expirar();
            return new(default, problema);
        }
    }

    private static async Task<Problema> LeerProblemaAsync(HttpResponseMessage r)
    {
        var estado = (int)r.StatusCode;
        try
        {
            var p = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
            var codigo = p.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var detalle = p.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var errores = p.TryGetProperty("errors", out var e) ? e.Deserialize<Dictionary<string, string[]>>(Json) : null;
            return new(estado, codigo, detalle ?? errores?.Values.FirstOrDefault()?.FirstOrDefault() ?? Generico(estado), errores);
        }
        catch (JsonException)
        {
            return new(estado, "", Generico(estado));
        }
    }

    private static string Generico(int estado) => estado switch
    {
        401 => "Su sesión terminó. Entre de nuevo con su PIN.",
        403 => "Su usuario no tiene permiso para esto.",
        404 => "No se encontró.",
        _ => "El servidor no pudo completar la operación. Intente de nuevo.",
    };
}

/// <summary>For calls that answer 204.</summary>
public sealed record Vacio;
