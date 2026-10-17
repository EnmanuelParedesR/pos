using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Abastalia.E2ETests;

namespace Abastalia.UiTests;

/// <summary>TK-309: the store's API connected to the cloud host, both real processes over real HTTP (Kestrel, not the in-memory test server), so
/// the cloud's node gate (R12 #1: Content-Length, per-IP limits, X-Body-Sha256) sees what a store really sends. Since R12 the store could not
/// register in the real cloud (411) and no test saw it. No browser.</summary>
public sealed class CloudConnectionTests
{
    private const string Email = "duena@esquina.do", Password = "Carniceria2026", Pin = "1234", Rnc = "131245678";

    [Fact]
    public async Task A_store_connects_uploads_pulls_its_license_and_backs_up_to_the_real_cloud()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"abastalia_cloudsync_{Guid.NewGuid():N}")).FullName;
        try
        {
            await RunAsync(temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { } // best effort: a temp folder left behind is harmless
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task RunAsync(string temp)
    {
        // ── The cloud, with a license key of its own (Key Vault in Azure) and a folder for the stores' backups ──
        using var cloudKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cloudSettings = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["Database__Provider"] = "Sqlite",
            ["Database__ConnectionString"] = $"Data Source={Path.Combine(temp, "cloud.db")};Pooling=False",
            ["Auth__RequireConfirmedEmail"] = "false",
            ["Billing__License__PrivateKey"] = cloudKey.ExportPkcs8PrivateKeyPem(),
            ["CloudBackup__Folder"] = Path.Combine(temp, "cloud-backups"),
        };
        await using var cloud = await WebProcess.HostAsync("Hosts", "Abastalia.Host.Cloud", cloudSettings, readyPath: "/health");
        AbastaliaServer? api = null;
        try
        {
            using var owner = new HttpClient { BaseAddress = cloud.Base };
            await Ok(await owner.PostAsJsonAsync("/api/auth/register", new { Email, Password }));
            await SignInAsync(owner);
            var tenantId = (await Ok(await owner.GetAsync("/api/me"))).GetProperty("tenantId").GetGuid();
            var storeId = (await Ok(await owner.PostAsJsonAsync("/api/stores", new { Name = "Colmado La Esquina" }))).GetProperty("id").GetGuid();
            async Task<string> Code() => (await Ok(await owner.PostAsync($"/api/stores/{storeId}/connection-codes", null))).GetProperty("code").GetString()!;

            // ── The store: trusts that cloud key; uploads every second and looks for a backup to copy every ~1 s ──
            var licenseFile = Path.Combine(temp, "license.jws");
            api = await AbastaliaServer.StartAsync(new()
            {
                ["Cloud__Url"] = cloud.Base.ToString(),
                ["License__CloudPublicKey"] = cloudKey.ExportSubjectPublicKeyInfoPem(),
                ["License__File"] = licenseFile,
                ["Backup__Folder"] = Path.Combine(temp, "backups"),
                ["Sync__IntervalSeconds"] = "1",
                ["Backup__CloudCheckMinutes"] = "0.02",
            });
            var device = (await Ok(await api.Client().PostAsJsonAsync("/api/identity/setup",
                new { AdministratorName = "Dueña", AdministratorCode = "admin", Pin, DeviceName = "PC de la tienda" }))).GetProperty("deviceToken").GetString()!;
            var token = (await Ok(await api.Client(device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin }))).GetProperty("token").GetString();
            using var admin = api.Client(device, token);
            await Ok(await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Colmado La Esquina", Rnc, DiscountCapPercent = 10m }));
            await Ok(await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = "una frase larga de respaldo" }));
            await Ok(await admin.PutAsJsonAsync("/api/backups/settings", new { CopyTo = (string?)null, Time = "23:30", Keep = 7, Automatic = true, Cloud = true }));
            await Ok(await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = 32, From = 1, To = 100, ExpiresOn = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1) }));
            var register = (await Ok(await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja 1" }))).GetProperty("id").GetGuid();
            var category = (await Ok(await admin.PostAsJsonAsync("/api/inventory/categories", new { Name = "Colmado", Color = "#0B5CAD", Notes = Array.Empty<string>() })))
                .GetProperty("id").GetGuid();
            var rice = (await Ok(await admin.PostAsJsonAsync("/api/inventory/products", new
            {
                Name = "Arroz 5 lb", CategoryId = category, Unit = "Unit", Plu = (string?)null, ItbisRate = "Exempt", Price = 285m, WholesalePrice = (decimal?)null, Cost = 230m,
                SellWithoutStock = false, MinimumStock = 0, Barcodes = Array.Empty<string>(), InitialStock = 10m,
            }))).GetProperty("id").GetGuid();

            // ── 1. Node registration with the connection code (the 411 of R12 #1) ──
            (await Ok(await admin.PostAsJsonAsync("/api/sync/connect", new { Code = await Code() }))).GetProperty("connected").GetBoolean().ShouldBeTrue();

            // ── 2. A sale goes up with the outbox and the cloud's replica (the panel) has it ──
            var shift = (await Ok(await admin.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 1000m }))).GetProperty("shiftId").GetGuid();
            await Ok(await admin.PostAsJsonAsync("/api/sales", new
            {
                SaleId = Guid.CreateVersion7(), ShiftId = shift, Lines = new[] { new { ProductId = rice, Quantity = 1m } },
                Payments = new[] { new { Method = "Cash", Amount = 285m } }, ExpectedTotal = 285m,
            }));
            var summary = await UntilAsync(async () => await Ok(await owner.GetAsync("/api/panel/summary")) is var s && s.GetProperty("salesCount").GetInt32() == 1 ? s : (JsonElement?)null,
                "the sale in the panel");
            summary.GetProperty("salesTotal").GetDecimal().ShouldBe(285m);

            // ── 3. A backup, encrypted on disk, copied as a file (signed X-Body-Sha256, the body checked after the signature) ──
            var backup = (await Ok(await admin.PostAsync("/api/backups", null))).GetProperty("fileName").GetString()!;
            await UntilAsync(async () => (await Ok(await owner.GetAsync($"/api/stores/{storeId}/backups"))).EnumerateArray()
                .Any(b => b.GetProperty("fileName").GetString() == backup) ? true : (bool?)null, "the backup in the panel");
            (await File.ReadAllBytesAsync(Path.Combine(temp, "cloud-backups", tenantId.ToString("N"), storeId.ToString("N"), backup)))
                .ShouldBe(await File.ReadAllBytesAsync(Path.Combine(temp, "backups", backup)));

            // ── 4. The license: Abastalia staff mark the business paid (platform console, 2FA), the store's next start sends its heartbeat and pulls
            //       the cloud's license, which carries the RNC the store sent ──
            await StaffMarksPaidAsync(cloudSettings, owner, tenantId);
            await api.RestartAsync();
            var features = await UntilAsync(async () => await Ok(await admin.GetAsync("/api/settings/features")) is var f && f.GetProperty("plan").GetString() is { Length: > 0 } ? f : (JsonElement?)null,
                "the cloud's license on the store");
            features.GetProperty("status").GetString().ShouldBe("Active", features.ToString());
            var license = Payload(await File.ReadAllTextAsync(licenseFile));
            license.GetProperty("rnc").GetString().ShouldBe(Rnc);
            license.GetProperty("tenantId").GetGuid().ShouldBe(tenantId);
            var checklist = await UntilAsync(async () => (await Ok(await owner.GetAsync($"/api/platform/stores/{storeId}"))).GetProperty("checklist") is var c
                && c.GetProperty("heartbeatReceived").GetBoolean() ? c : (JsonElement?)null, "the heartbeat in the platform console");
            checklist.GetProperty("rncSet").GetBoolean().ShouldBeTrue();

            // ── 5. The gate itself, as a second PC of the store with its own key ──
            await GateAsync(cloud.Base, await Code(), cloudKey, tenantId);

            api.Errors500.ShouldBeEmpty();
        }
        catch (Exception e) when (e is ShouldAssertException or HttpRequestException or InvalidOperationException or TimeoutException)
        {
            throw new InvalidOperationException($"{e.Message}\n── Cloud host ──\n{cloud.Output()}\n── Store API ──\n{api?.Output()}", e);
        }
        finally
        {
            if (api is not null) await api.DisposeAsync();
        }
    }

    /// <summary>The owner is also Abastalia staff here: granted with the CLI command, 2FA on, signed in again with the code (R11 B-Med6), then the
    /// staff override "paid through" (TK-249) a month ahead.</summary>
    private static async Task StaffMarksPaidAsync(Dictionary<string, string> settings, HttpClient owner, Guid tenantId)
    {
        var key = (await Ok(await owner.PostAsJsonAsync("/api/auth/manage/2fa", new { }))).GetProperty("sharedKey").GetString()!;
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        await Ok(await owner.PostAsJsonAsync("/api/auth/manage/2fa", new { enable = true, twoFactorCode = Totp(key, step) }));
        await WebProcess.RunHostAsync("Hosts", "Abastalia.Host.Cloud", $"grant-platform-admin {Email}", settings);
        await SignInAsync(owner, Totp(key, step + 1)); // R12 #4: each code once; the cloud accepts two steps ahead
        await Ok(await owner.PutAsJsonAsync($"/api/platform/tenants/{tenantId}/billing", new { ManualUntilUtc = DateTime.UtcNow.AddDays(30), Note = "TK-309: prueba" }));
    }

    private static async Task SignInAsync(HttpClient owner, string? twoFactorCode = null)
    {
        owner.DefaultRequestHeaders.Authorization = null;
        var login = await Ok(await owner.PostAsJsonAsync("/api/auth/login", new { Email, Password, twoFactorCode }));
        owner.DefaultRequestHeaders.Authorization = new("Bearer", login.GetProperty("accessToken").GetString());
    }

    /// <summary>R12 #1 from outside: a body without its length is refused (411); the license answer carries the cloud's signed time for this
    /// nonce and no trial (not referred); a revoked node is refused with the body never sent, so before any of it was read.</summary>
    private static async Task GateAsync(Uri cloud, string code, ECDsa cloudKey, Guid tenantId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var http = new HttpClient { BaseAddress = cloud };
        var node = (await Ok(await http.PostAsync("/api/nodes/register", Json(new { code, nodeName = "PC de la oficina", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }))))
            .GetProperty("nodeId").GetGuid();

        // Chunked (JsonContent does not know its length): what the store sent before TK-307.
        var chunked = await http.PostAsJsonAsync("/api/nodes/register", new { code = "12345678", nodeName = "x" });
        chunked.StatusCode.ShouldBe(HttpStatusCode.LengthRequired);

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var answer = await Ok(await SignedAsync(http, key, node, "/api/nodes/license", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { rnc = Rnc, nonce }))));
        answer.GetProperty("trial").ValueKind.ShouldBe(JsonValueKind.Null);
        var time = answer.GetProperty("time").GetString()!;
        Verifies(time, cloudKey).ShouldBeTrue();
        var told = Payload(time);
        (told.GetProperty("tenantId").GetGuid(), told.GetProperty("nonce").GetString()).ShouldBe((tenantId, nonce));
        (told.GetProperty("utc").GetDateTime() - DateTime.UtcNow).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
        Payload(answer.GetProperty("license").GetString()!).GetProperty("rnc").GetString().ShouldBe(Rnc);

        (await SignedAsync(http, key, node, "/api/nodes/revoke", [])).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Revoked: headers of a 50 MB backup, signed, and not a byte of the body. The answer comes anyway.
        const string path = "/api/nodes/backups/abastalia-20261017-030000.abastalia-backup";
        var claimed = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(cloud.Host, cloud.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST {path} HTTP/1.1\r\nHost: {cloud.Authority}\r\nAuthorization: Node {Sign(key, node, path, claimed)}\r\nX-Body-Sha256: {claimed}\r\n" +
            "Content-Type: application/octet-stream\r\nContent-Length: 52428800\r\n\r\n"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer, timeout.Token);
        Encoding.ASCII.GetString(buffer, 0, read).ShouldStartWith("HTTP/1.1 401");
    }

    private static Task<HttpResponseMessage> SignedAsync(HttpClient http, ECDsa key, Guid node, string path, byte[] body)
    {
        var hash = Base64Url(SHA256.HashData(body));
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Node", Sign(key, node, path, hash));
        request.Headers.Add("X-Body-Sha256", hash);
        return http.SendAsync(request);
    }

    /// <summary>As the store's CloudClient signs (Host.Cloud NodeSignature).</summary>
    private static string Sign(ECDsa key, Guid node, string path, string bodyHash)
    {
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        return $"{node}.{seconds}.{nonce}.{Base64Url(key.SignData(Encoding.UTF8.GetBytes($"{node}.{seconds}.{nonce}.POST {path} {bodyHash}"), HashAlgorithmName.SHA256))}";
    }

    private static ByteArrayContent Json(object value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static bool Verifies(string jws, ECDsa key)
    {
        var parts = jws.Split('.');
        return key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]), HashAlgorithmName.SHA256);
    }

    private static JsonElement Payload(string jws) => JsonDocument.Parse(FromBase64Url(jws.Trim().Split('.')[1])).RootElement.Clone();

    /// <summary>RFC 6238, as an authenticator app (6 digits, 30 s).</summary>
    private static string Totp(string base32, long step)
    {
        var bits = string.Concat(base32.ToUpperInvariant().Where(c => c is not (' ' or '='))
            .Select(c => Convert.ToString("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c), 2).PadLeft(5, '0')));
        var secret = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
#pragma warning disable CA5350 // RFC 6238 is HMAC-SHA1
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The background workers (uploader, backup copy, heartbeat) and the cloud's applier take a moment: up to 60 s.</summary>
    private static async Task<T> UntilAsync<T>(Func<Task<T?>> probe, string what) where T : struct
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } found) return found;
            await Task.Delay(250); // waiting on another process's background worker: nothing to await instead
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.ShouldBeTrue($"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery} → {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
