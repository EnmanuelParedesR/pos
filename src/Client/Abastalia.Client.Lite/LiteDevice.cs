using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.Lite;

/// <summary>TK-324: this browser as the business's selling device, through js/lite.js (lite-signer): its pairing, its signed requests to
/// Abastalia.Host.Lite and its e-CF signer. Neither key nor the .p12's password ever reaches .NET.</summary>
public sealed class LiteDevice(IJSRuntime js, LiteOptions options)
{
    private Task<IJSObjectReference>? _module;

    private Task<IJSObjectReference> Module() => _module ??= Load();

    private async Task<IJSObjectReference> Load()
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/lite.js");
        await module.InvokeVoidAsync("init", new { api = options.Api.ToString().TrimEnd('/'), sendToDgii = options.SendToDgii });
        return module;
    }

    public async Task<string?> DeviceIdAsync() => await (await Module()).InvokeAsync<string?>("deviceId");

    public sealed record Paired(string? DeviceId, string? Error);

    public async Task<Paired> PairAsync(string code, string name) => await (await Module()).InvokeAsync<Paired>("pair", code, name);

    public async Task ForgetAsync() => await (await Module()).InvokeVoidAsync("forget");

    public sealed record Fetched(int Status, string? Body, string? ContentType, string? Disposition);

    public async Task<Fetched> FetchAsync(string method, string pathAndQuery, Dictionary<string, string> headers, string? body, CancellationToken ct) =>
        await (await Module()).InvokeAsync<Fetched>("fetchSigned", ct, method, pathAndQuery, headers, body);

    public async Task<bool> HasCertificateAsync() => await (await Module()).InvokeAsync<bool>("hasCertificate");

    public sealed record Imported(bool Persisted, string? Error);

    public async Task<Imported> ImportCertificateAsync(ElementReference file, ElementReference password) =>
        await (await Module()).InvokeAsync<Imported>("importCertificate", file, password);

    /// <summary>What a pass left: each due e-CF as the host has it now, or why it could not run ("KeyMissing": import the .p12).</summary>
    public sealed record FiscalPass(string? Error, List<PendingEcf> Items);

    public sealed record PendingEcf(string ENcf, string Step, string Status, DateTime SubmitDeadlineUtc, bool DeadlineNear, string? LastError);

    public async Task<FiscalPass> ProcessFiscalAsync(string token) => await (await Module()).InvokeAsync<FiscalPass>("processFiscal", token);
}

/// <param name="Api">Abastalia.Host.Lite (wwwroot/appsettings.json, <c>Api:Url</c>).</param>
/// <param name="SendToDgii">False until TK-141 opens TesteCF: the device signs and the host keeps them, nothing goes to the DGII.</param>
public sealed record LiteOptions(Uri Api, bool SendToDgii, TimeSpan FiscalInterval);

/// <summary>Every call of Client.UI's <c>AbastaliaApi</c> goes out through <see cref="LiteDevice.FetchAsync"/>: signed by the device.</summary>
internal sealed class DeviceFetchHandler(Func<LiteDevice> device) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : Convert.ToBase64String(await request.Content.ReadAsByteArrayAsync(cancellationToken));
        // X-Device is the store's device secret: Host.Lite ignores it, and its CORS policy does not allow it.
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .Where(h => h.Key != "X-Device").ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
        var r = await device().FetchAsync(request.Method.Method, request.RequestUri!.PathAndQuery, headers, body, cancellationToken);
        if (r.Status == 0) throw new HttpRequestException("Abastalia.Host.Lite did not answer.");
        var content = new ByteArrayContent(r.Body is null ? [] : Convert.FromBase64String(r.Body));
        if (r.ContentType is not null && MediaTypeHeaderValue.TryParse(r.ContentType, out var type)) content.Headers.ContentType = type;
        if (r.Disposition is not null && ContentDispositionHeaderValue.TryParse(r.Disposition, out var disposition)) content.Headers.ContentDisposition = disposition;
        return new HttpResponseMessage((HttpStatusCode)r.Status) { Content = content, RequestMessage = request };
    }
}

/// <summary>Client.UI's local store on the Lite app. "device" is the pairing kept by lite-signer (its id once paired; clearing it forgets the
/// device's key); everything else is small and not secret (the hardware choice, a pending setup step) and lives in localStorage.</summary>
internal sealed class LiteStore(LiteDevice device, IJSRuntime js) : ILocalStore
{
    private const string Device = "device", Prefix = "abastalia.";

    public async Task<string?> ReadAsync(string key) =>
        key == Device ? await device.DeviceIdAsync() : await js.InvokeAsync<string?>("localStorage.getItem", Prefix + key);

    public async Task SaveAsync(string key, string? value)
    {
        if (key == Device)
        {
            if (value is null) await device.ForgetAsync(); // pairing itself happens in lite.js, which keeps the key
            return;
        }
        if (value is null) await js.InvokeVoidAsync("localStorage.removeItem", Prefix + key);
        else await js.InvokeVoidAsync("localStorage.setItem", Prefix + key, value);
    }
}
