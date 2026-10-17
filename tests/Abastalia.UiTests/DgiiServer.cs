using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.UiTests;

/// <summary>TK-365: the integration tests' <see cref="FakeDgii"/> over real HTTP on 127.0.0.1, for the API process (E2E only:
/// <c>Fiscal:Dgii:TestServer</c>). https://ecf.dgii.gov.do/testecf/x arrives as /ecf.dgii.gov.do/testecf/x and is answered by the fake as that URL.</summary>
public sealed class DgiiServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpMessageInvoker _fake;
    private volatile TaskCompletionSource _gate = Open();

    public FakeDgii Dgii { get; } = new();
    public Uri Base { get; }

    private DgiiServer(WebApplication app, Uri @base) => (_app, Base, _fake) = (app, @base, new HttpMessageInvoker(Dgii));

    /// <summary>Until <see cref="Release"/>, an e-CF sent to the DGII waits for its answer (so the queue shows it "por enviar").</summary>
    public void Hold() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    private readonly List<string> _asked = [];

    /// <summary>Every path the API asked for, held or not (for a failure message).</summary>
    public List<string> Requests()
    {
        lock (_asked) return [.. _asked];
    }

    private static TaskCompletionSource Open()
    {
        var open = new TaskCompletionSource();
        open.SetResult();
        return open;
    }

    public static async Task<DgiiServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear(); // the appsettings next to these tests are the hosts' (Kestrel with a certificate)
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        var @base = new Uri($"http://127.0.0.1:{WebProcess.FreePort()}/");
        builder.WebHost.UseUrls(@base.ToString());
        var app = builder.Build();
        var server = new DgiiServer(app, @base);
        app.Run(server.ForwardAsync);
        await app.StartAsync();
        return server;
    }

    private async Task ForwardAsync(HttpContext http)
    {
        var path = http.Request.Path.Value!;
        lock (_asked) _asked.Add(path);
        var slash = path.IndexOf('/', 1);
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), $"https://{path[1..slash]}{path[slash..]}{http.Request.QueryString}");
        if (http.Request.Headers.Authorization.ToString() is { Length: > 0 } auth) request.Headers.TryAddWithoutValidation("Authorization", auth);
        if (http.Request.HasFormContentType)
        {
            // The fake reads the upload as the DGII client sends it: multipart with the file field "xml".
            var multipart = new MultipartFormDataContent();
            foreach (var file in (await http.Request.ReadFormAsync()).Files) multipart.Add(new StreamContent(file.OpenReadStream()), file.Name, file.FileName);
            request.Content = multipart;
        }
        if (path.Contains("/recepcion", StringComparison.OrdinalIgnoreCase)) await _gate.Task.WaitAsync(http.RequestAborted);
        using var response = await _fake.SendAsync(request, http.RequestAborted);
        http.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } type) http.Response.ContentType = type.ToString();
        await response.Content.CopyToAsync(http.Response.Body);
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        await _app.DisposeAsync();
        _fake.Dispose();
    }
}
