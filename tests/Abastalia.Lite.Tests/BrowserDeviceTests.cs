using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Abastalia.Lite.Tests;

/// <summary>The Lite host on Kestrel, for a client outside the process.</summary>
public sealed class KestrelLiteApp : LiteApp
{
    public KestrelLiteApp() => UseKestrel();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Kestrel:Endpoints:Http:Url", "http://127.0.0.1:0");
    }

    public string Address
    {
        get
        {
            StartServer();
            return Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        }
    }
}

/// <summary>TK-321: the browser side (src/Client/lite-signer/device.js, run by Node with its WebCrypto) pairs with a code from the panel and its
/// signed requests open a session on the real host: the two sides agree on the format byte for byte. Needs <c>node</c>; skips otherwise.</summary>
public sealed class BrowserDeviceTests(KestrelLiteApp app) : IClassFixture<KestrelLiteApp>
{
    private const string Script = """
        import { pairDevice, deviceFetch } from './device.js';
        import { memoryStore } from './store.js';
        let input = '';
        for await (const chunk of process.stdin) input += chunk;
        const { baseUrl, code, pin } = JSON.parse(input);
        const store = memoryStore();
        const { deviceId } = await pairDevice({ baseUrl, code, name: 'Navegador', store });
        const send = deviceFetch({ baseUrl, store });
        const session = await send('/api/identity/sessions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: 'dueno', pin }) });
        const { token } = await session.json();
        const me = await send('/api/identity/me?view=1', { headers: { Authorization: `Bearer ${token}` } });
        process.stdout.write(JSON.stringify({ deviceId, session: session.status, me: me.status, extractable: (await store.getDevice()).key.extractable }));
        """;

    [Fact]
    public async Task The_browser_pairs_and_signs_as_the_host_checks()
    {
        var baseUrl = app.Address;
        var tenantId = Guid.CreateVersion7();
        (await app.Provision(new { TenantId = tenantId, OwnerName = "Dueña", OwnerCode = "dueno", LiteApp.Pin })).EnsureSuccessStatusCode();
        var code = await app.CodeAsync(tenantId);

        using var result = await NodeAsync(JsonSerializer.Serialize(new { baseUrl, code, pin = LiteApp.Pin }));
        result.RootElement.GetProperty("session").GetInt32().ShouldBe(200);
        result.RootElement.GetProperty("me").GetInt32().ShouldBe(200);
        result.RootElement.GetProperty("extractable").GetBoolean().ShouldBeFalse();
        var listed = await app.Panel().GetFromJsonAsync<JsonElement>($"/internal/tenants/{tenantId}/devices");
        listed.GetProperty("devices").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(result.RootElement.GetProperty("deviceId").GetGuid());
    }

    private static async Task<JsonDocument> NodeAsync(string input)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Abastalia.slnx"))) root = root.Parent!;
        var start = new ProcessStartInfo("node")
        {
            ArgumentList = { "--input-type=module", "-e", Script },
            WorkingDirectory = Path.Combine(root.FullName, "src", "Client", "lite-signer"),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        Process node;
        try { node = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("node is not installed."); throw; }
        using (node)
        {
            await node.StandardInput.WriteAsync(input);
            node.StandardInput.Close();
            var output = node.StandardOutput.ReadToEndAsync();
            var error = await node.StandardError.ReadToEndAsync();
            await node.WaitForExitAsync();
            if (node.ExitCode != 0) throw new InvalidOperationException($"node failed: {error}");
            return JsonDocument.Parse(await output);
        }
    }
}
