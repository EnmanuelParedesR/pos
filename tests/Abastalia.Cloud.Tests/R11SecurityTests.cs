using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Abastalia.Host.Cloud.Assistant;
using Abastalia.Host.Cloud.Fe;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Sales.Contracts;

namespace Abastalia.Cloud.Tests;

/// <summary>R11 B (backlog/reviews/R11-seguridad.md): the cloud with small caps, so each one is reached in a few calls.</summary>
public sealed class StrictCloudApp : CloudApp
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ingest:MaxUnknownPending", "1");
        builder.UseSetting("Ingest:MaxEventsPerTenantDay", "6");
        builder.UseSetting("Ingest:BatchesPerMinutePerNode", "4");
        builder.UseSetting("Archive:UploadsPerMinutePerNode", "1");
        builder.UseSetting("Auth:ManagePerMinutePerUser", "2");
        builder.UseSetting("Nodes:FailedCodesPer10Minutes", "3");
    }
}

public sealed class R11SecurityTests(StrictCloudApp app) : IClassFixture<StrictCloudApp>
{
    private DateTime Now => app.Clock.GetUtcNow().UtcDateTime;

    private static object Event(string type, object payload) =>
        new { id = Guid.NewGuid(), type, aggregateId = Guid.NewGuid(), occurredUtc = DateTime.UtcNow, payload };

    [Fact]
    public async Task B_Med3_ingest_refuses_big_events_foreign_types_and_past_the_caps_per_business_and_PC()
    {
        var store = await app.StoreWithNodeAsync(await app.OwnerAsync());
        (await (await store.SendAsync(Event("Abastalia.Sales.Contracts.SaleRecordedV2", new { text = new string('x', 70_000) }))).ErrorCode()).ShouldBe("Ingest.EventTooLarge");
        (await (await store.SendAsync(Event("Otro.Evento", new { }))).ErrorCode()).ShouldBe("Ingest.InvalidEvent");

        // A type Abastalia does not know yet waits, but only up to the cap per business.
        (await store.SendAsync(Event("Abastalia.Futuro.EventoV9", new { }))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await store.SendAsync(Event("Abastalia.Futuro.EventoV9", new { }))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Per PC and minute (4 batches: the PC is cut before it can do more).
        (await store.SendAsync(Events.Envelope(Sale(), Now))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Per business and day: a second PC of the same business reaches the business's cap (6 events).
        var owner2 = await app.OwnerAsync();
        var a = await app.StoreWithNodeAsync(owner2);
        (await a.SendAsync([.. Enumerable.Range(0, 5).Select(_ => Events.Envelope(Sale(), Now))])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var refused = await a.SendAsync(Events.Envelope(Sale(), Now), Events.Envelope(Sale(), Now));
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await refused.ErrorCode()).ShouldBe("Ingest.Cap");
    }

    [Fact]
    public async Task B_Med3_archive_uploads_are_limited_per_PC()
    {
        var store = await app.StoreWithNodeAsync(await app.OwnerAsync());
        (await store.Client.PostAsJsonAsync("/fiscal/v1/archive", new { })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await store.Client.PostAsJsonAsync("/fiscal/v1/archive", new { })).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task B_Med4_a_store_never_replaces_or_voids_another_stores_rows_of_the_same_business()
    {
        var owner = await app.OwnerAsync();
        var main = await app.StoreWithNodeAsync(owner, "Principal");
        var branch = await app.StoreWithNodeAsync(owner, "Sucursal");
        var sale = Sale(total: 900m);
        (await main.SendAsync(Events.Envelope(sale, Now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();

        (await branch.SendAsync(Events.Envelope(sale with { Total = 1m, EventId = Guid.NewGuid() }, Now.AddMinutes(1)),
            Events.Envelope(new SaleVoidedV1(sale.SaleId, "E340000000001", "Robo", null), Now.AddMinutes(2)))).EnsureSuccessStatusCode();
        await app.ApplyAsync();

        var kept = await app.InDbAsync(db => db.Sales.SingleAsync(s => s.Id == sale.SaleId));
        (kept.StoreId, kept.Total).ShouldBe((main.StoreId, 900m));
        (await app.InDbAsync(db => db.SaleVoids.AnyAsync(v => v.SaleId == sale.SaleId))).ShouldBeFalse();
    }

    [Fact]
    public async Task B_Low2_a_projection_error_keeps_only_its_kind_never_the_values()
    {
        var store = await app.StoreWithNodeAsync(await app.OwnerAsync());
        (await store.SendAsync(Event(typeof(SaleRecordedV2).FullName!, new { saleId = Guid.NewGuid(), total = "Juana Pérez 809-555-0101" }))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        var error = await app.InDbAsync(db => db.Inbox.Where(i => i.NodeId == store.NodeId && i.Error != null).Select(i => i.Error).SingleAsync());
        error.ShouldBe("JsonException at $.total");
    }

    [Fact]
    public async Task B_Low5_connection_codes_few_live_per_business_and_wrong_ones_counted_together()
    {
        var owner = await app.OwnerAsync();
        var store = (await (await owner.Client.PostAsJsonAsync("/api/stores", new { name = "Tienda" })).Json()).GetProperty("id").GetGuid();
        var codes = new List<string>();
        for (var i = 0; i < 5; i++)
            codes.Add((await (await owner.Client.PostAsync($"/api/stores/{store}/connection-codes", null)).Json()).GetProperty("code").GetString()!);
        (await (await owner.Client.PostAsync($"/api/stores/{store}/connection-codes", null)).ErrorCode()).ShouldBe("Stores.TooManyCodes");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Task<HttpResponseMessage> Register(string code) => app.CreateClient().PostAsJsonAsync("/api/nodes/register",
            new { code, nodeName = "PC", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        foreach (var wrong in new[] { "00000000", "00000001", "00000002" }.Where(c => !codes.Contains(c)))
            (await (await Register(wrong)).ErrorCode()).ShouldBe("Nodes.InvalidCode");
        // Three wrong codes from anywhere: every attempt waits for the window, even a right one.
        (await (await Register(codes[0])).ErrorCode()).ShouldBe("Nodes.TooManyAttempts");
        app.Clock.Advance(TimeSpan.FromMinutes(11));
        var other = await app.OwnerAsync();
        var fresh = (await (await other.Client.PostAsJsonAsync("/api/stores", new { name = "Otra" })).Json()).GetProperty("id").GetGuid();
        var code = (await (await other.Client.PostAsync($"/api/stores/{fresh}/connection-codes", null)).Json()).GetProperty("code").GetString()!;
        (await Register(code)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task B_Med2_account_management_is_limited_per_user()
    {
        var owner = await app.OwnerAsync();
        for (var i = 0; i < 2; i++) (await owner.Client.GetAsync("/api/auth/manage/info")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Client.PostAsJsonAsync("/api/auth/manage/info", new { newEmail = "otra@example.com" })).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task B_Low6_wrong_passwords_block_that_IP_and_account_for_a_while_without_locking_the_account()
    {
        var owner = await app.OwnerAsync();
        var client = app.CreateClient();
        for (var i = 0; i < 5; i++)
            (await client.PostAsJsonAsync("/api/auth/login", new { email = owner.Email, password = "Incorrecta1" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var blocked = await client.PostAsJsonAsync("/api/auth/login", new { email = owner.Email, password = CloudApp.Password });
        (await blocked.Json()).GetProperty("detail").GetString().ShouldBe("LockedOut");
        (await app.InDbAsync(db => db.Users.Where(u => u.Email == owner.Email).Select(u => u.LockoutEnd).SingleAsync())).ShouldBeNull(); // not the account itself

        app.Clock.Advance(TimeSpan.FromMinutes(16));
        (await client.PostAsJsonAsync("/api/auth/login", new { email = owner.Email, password = CloudApp.Password })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task B_Low8_the_paid_extras_trust_a_license_Abastalia_signed_or_the_clouds_own_trial_dates()
    {
        using var abastalia = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["License:PublicKey"] = abastalia.ExportSubjectPublicKeyInfoPem() }).Build();
        var check = new LicenseCheck(config, app.Services.GetRequiredService<IHostEnvironment>(), app.Clock);
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner);
        Task<bool> Allowed() => app.InDbAsync(db => check.AllowsPaidExtrasAsync(db, owner.TenantId, CancellationToken.None));

        (await Allowed()).ShouldBeTrue(); // no sale yet: the trial has not started
        (await store.SendAsync(Events.Envelope(Sale(), Now.AddDays(-20)))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        (await Allowed()).ShouldBeFalse(); // the store's word ("Active") is not enough: no signed license, trial over

        async Task Beat(string? jws, string status = "Active") => await app.InDbAsync(async db =>
        {
            await db.NodeHeartbeats.Where(h => h.NodeId == store.NodeId).ExecuteDeleteAsync();
            db.NodeHeartbeats.Add(new ReplicaNodeHeartbeat { TenantId = owner.TenantId, NodeId = store.NodeId, StoreId = store.StoreId, SentUtc = Now, LicenseStatus = status, LicenseJws = jws });
            return await db.SaveChangesAsync();
        });
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Beat(Jws(forger, Now.AddDays(30), owner.TenantId));
        (await Allowed()).ShouldBeFalse();
        await Beat(Jws(abastalia, Now.AddDays(-20), owner.TenantId));
        (await Allowed()).ShouldBeFalse(); // expired beyond the 15 days of grace
        // TK-268: another business's license, or one bound to no business, does not pass.
        await Beat(Jws(abastalia, Now.AddDays(30), Guid.NewGuid()));
        (await Allowed()).ShouldBeFalse();
        await Beat(Jws(abastalia, Now.AddDays(30), null));
        (await Allowed()).ShouldBeFalse();
        await Beat(Jws(abastalia, Now.AddDays(30), owner.TenantId));
        (await Allowed()).ShouldBeTrue();
        await Beat(Jws(abastalia, Now.AddDays(30), owner.TenantId), status: "Restricted");
        (await Allowed()).ShouldBeFalse(); // a store that says it is restricted is believed
    }

    [Fact]
    public void B_Med1_the_chain_downloads_nothing_and_revocation_goes_only_to_the_authorities_hosts()
    {
        // The INDOTEL authorities name their own CRL and OCSP hosts: those are the allowed ones.
        var config = app.Services.GetRequiredService<IConfiguration>();
        var avansi = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(config.GetSection("Fe:TrustedRoots").Get<string[]>()![1]));
        FeTrust.RevocationUrls(avansi).Select(u => u.Host).ShouldBe(["ocsp.avansi.com.do", "crl.avansi.com.do", "crl2.avansi.com.do"], ignoreOrder: true);

        // A signer whose CRL points elsewhere (an internal address, a slow host) is refused before anything is fetched.
        var trust = new FeTrust(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fe:TrustedRoots:0"] = TestPki.RootBase64, ["Fe:CheckRevocation"] = "true",
        }).Build(), app.Services.GetRequiredService<IHostEnvironment>(), app.Clock, NullLogger<FeTrust>.Instance);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Firmante, SERIALNUMBER=101672919", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["http://169.254.169.254/latest.crl"]));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(TestPki.Root, true, false));
        using var signer = request.Create(TestPki.Root, Now.AddDays(-1), Now.AddYears(1), RandomNumberGenerator.GetBytes(12));
        FeTrust.RevocationUrls(signer).ShouldHaveSingleItem().Host.ShouldBe("169.254.169.254");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        trust.IsTrusted(signer).ShouldBeFalse();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static SaleRecordedV2 Sale(decimal total = 100m) => new(Guid.CreateVersion7(), 1, Guid.NewGuid(), Guid.NewGuid(), null, "E320000000001", total, 0m, 0m,
        [new SaleLineV2(Guid.NewGuid(), null, "Pollo", 1, "Unit", total, 0m, 50m)], [new SalePaymentV1("Cash", total)]);

    /// <summary>A license as Abastalia issues it (compact JWS, ES256), with the store's fields.</summary>
    private static string Jws(ECDsa key, DateTime expiresUtc, Guid? tenantId)
    {
        static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64("""{"alg":"ES256","typ":"JWT"}"""u8.ToArray());
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new { store = "Carnicería", plan = "Default", features = Array.Empty<string>(), limits = new { }, issuedUtc = expiresUtc.AddYears(-1), expiresUtc,
            tenantId }, Web));
        return $"{header}.{body}.{B64(key.SignData(Encoding.ASCII.GetBytes($"{header}.{body}"), HashAlgorithmName.SHA256))}";
    }
}
