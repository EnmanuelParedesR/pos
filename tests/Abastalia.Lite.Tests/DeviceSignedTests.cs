using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;

namespace Abastalia.Lite.Tests;

/// <summary>A Lite business ready to sell: its owner's client, a product of RD$118 (ITBIS included) and an open shift.</summary>
public sealed record Shop(Owner Owner, HttpClient Client, Guid ProductId, Guid ShiftId);

/// <summary>TK-323: the device's side, as the browser signer (src/Client/lite-signer) does it. The documents are signed with the store's
/// <see cref="CertificateEcfSigner"/>, which LiteBrowserSigningTests proves byte for byte the same as the browser's.</summary>
internal static class Shops
{
    public const string Rnc = "131245678";

    /// <summary>A throwaway self-signed certificate; SERIALNUMBER = the holder's RNC or cédula, as on a real one (null: none).</summary>
    public static X509Certificate2 Certificate(string? serialNumber = Rnc) =>
        new CertificateRequest(serialNumber is null ? "CN=Sin RNC" : $"CN=Firmante de Prueba, SERIALNUMBER={serialNumber}", RSA.Create(2048),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    public static (string Xml, string Code) Sign(X509Certificate2 certificate, string xml) => new CertificateEcfSigner(certificate).Sign(XDocument.Parse(xml));

    /// <param name="environment">The business's own DGII environment; Simulated keeps the default (never chosen).</param>
    public static async Task<Shop> OpenAsync(LiteApp app, string owner, string rnc = Rnc, string environment = "TesteCF", string? connectionCode = null)
    {
        var o = await app.BusinessAsync(owner, connectionCode);
        var c = app.Client(o);
        await OkAsync(await c.PutAsJsonAsync("/api/settings/business", new { Name = owner, Rnc = rnc, Address = "Av. Duarte 25", DiscountCapPercent = 10 }));
        await OkAsync(environment == "Simulated"
            ? await c.PostAsJsonAsync("/api/fiscal/sequences", new { Type = 32, From = 1, To = 1000 })
            : await c.PutAsJsonAsync("/api/fiscal/mode", new { Environment = environment, Confirm = true }));
        var category = await IdAsync(await c.PostAsJsonAsync("/api/inventory/categories", new { Name = "Res", Color = "#B42318", Notes = Array.Empty<string>() }));
        var product = await IdAsync(await c.PostAsJsonAsync("/api/inventory/products", new
        {
            Name = "Carne", CategoryId = category, Unit = "Unit", ItbisRate = "General18", Price = 118m, Cost = 0, SellWithoutStock = true, MinimumStock = 0,
            Barcodes = Array.Empty<string>(), InitialStock = 0,
        }));
        var register = await IdAsync(await c.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja" }));
        var shift = await c.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 0 });
        await OkAsync(shift);
        return new Shop(o, c, product, (await shift.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("shiftId").GetGuid());
    }

    /// <summary>A cash sale of RD$118: a 32 under RD$250,000 (its RFCE goes to the DGII), or a 31 with a buyer.</summary>
    public static async Task<string> SellAsync(this Shop s, string? buyerRnc = null)
    {
        var r = await s.Client.PostAsJsonAsync("/api/sales", new
        {
            SaleId = Guid.CreateVersion7(), s.ShiftId, Lines = new[] { new { s.ProductId, Quantity = 1 } }, Payments = new[] { new { Method = "Cash", Amount = 118m } },
            ExpectedTotal = 118m, DocumentType = buyerRnc is null ? "Consumer32" : "FiscalCredit31", CustomerRnc = buyerRnc, CustomerName = buyerRnc is null ? null : "Cliente SRL",
        });
        await OkAsync(r);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eNcf").GetString()!;
    }

    /// <summary>A new session on the same device (one outlives its maximum age when a test moves the clock days ahead).</summary>
    public static async Task<Shop> SignInAgainAsync(this Shop s, LiteApp app)
    {
        var session = await app.Device(s.Owner.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = "dueno", LiteApp.Pin });
        await OkAsync(session);
        var token = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        return s with { Client = app.Client(s.Owner, token) };
    }

    public static async Task<List<JsonElement>> PendingAsync(this Shop s) =>
        [.. (await s.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/device/documents")).EnumerateArray()];

    public static async Task<JsonElement> PendingAsync(this Shop s, string encf) => (await s.PendingAsync()).Single(d => d.GetProperty("eNcf").GetString() == encf);

    public static Task<HttpResponseMessage> SubmitAsync(this Shop s, Guid id, string? signedXml = null, string? signedRfce = null, object? answer = null) =>
        s.Client.PostAsJsonAsync($"/api/fiscal/device/documents/{id}", new { SignedXml = signedXml, SignedRfce = signedRfce, Answer = answer });

    public static async Task<JsonElement> OkJsonAsync(this Task<HttpResponseMessage> request)
    {
        var r = await request;
        await OkAsync(r);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<string> ProblemCodeAsync(this Task<HttpResponseMessage> request, HttpStatusCode status)
    {
        var r = await request;
        r.StatusCode.ShouldBe(status, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    public static async Task<FiscalDocument> DocumentAsync(this LiteApp app, Owner o, string encf)
    {
        await using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(o.TenantId);
        return await scope.ServiceProvider.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().SingleAsync(d => d.ENcf == encf);
    }

    public static string Text(this JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static async Task OkAsync(HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{r.RequestMessage?.RequestUri} {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    private static async Task<Guid> IdAsync(HttpResponseMessage r)
    {
        await OkAsync(r);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}

/// <summary>TK-323 (ADR-0013, flow of a sale): the sale leaves its e-CF unsigned for the business's device; the device signs it, sends it to
/// the DGII itself and reports back; the server checks every signature before it keeps it.</summary>
public sealed class DeviceSignedTests(LiteApp app) : IClassFixture<LiteApp>
{
    [Fact]
    public async Task A_31_is_signed_by_the_device_sent_polled_accepted_and_archived()
    {
        var shop = await Shops.OpenAsync(app, "Dueña del colmado", connectionCode: "33333333");
        var encf = await shop.SellAsync(buyerRnc: "101672919");

        var pending = await shop.PendingAsync(encf);
        (pending.Text("step"), pending.Text("status"), pending.Text("channel")).ShouldBe(("Sign", "AwaitingDeviceSignature", "Ecf"));
        pending.GetProperty("securityCode").ValueKind.ShouldBe(JsonValueKind.Null);
        pending.GetProperty("deadlineNear").GetBoolean().ShouldBeFalse();
        var stored = await app.DocumentAsync(shop.Owner, encf);
        (stored.Status, stored.SecurityCode, stored.Environment).ShouldBe((SubmissionStatus.AwaitingDeviceSignature, "", FiscalEnvironment.TesteCF));
        EcfSubmissionQueue.Unzip(stored.SignedXml).ShouldBe(pending.Text("xml")); // no certificate on the server: nothing signed there
        pending.Text("xml").ShouldNotContain("Signature");

        var (signed, code) = Shops.Sign(Shops.Certificate(), pending.Text("xml"));
        var id = pending.GetProperty("id").GetGuid();
        var sent = await shop.SubmitAsync(id, signed, answer: new { State = "InProcess", TrackId = "TRACK-1" }).OkJsonAsync();
        (sent.Text("step"), sent.Text("status"), sent.Text("securityCode"), sent.Text("trackId")).ShouldBe(("Poll", "Sent", code, "TRACK-1"));

        // The store's poll delay before it is offered again; then the final answer.
        (await shop.PendingAsync()).ShouldNotContain(d => d.Text("eNcf") == encf);
        app.Clock.Advance(EcfSubmissionQueue.PollDelay(0));
        (await shop.PendingAsync(encf)).Text("step").ShouldBe("Poll");
        var done = await shop.SubmitAsync(id, answer: new { State = "Accepted", TrackId = "TRACK-1" }).OkJsonAsync();
        (done.Text("step"), done.Text("status")).ShouldBe(("Done", "Accepted"));
        (await shop.SubmitAsync(id, answer: new { State = "Rejected" }).ProblemCodeAsync(HttpStatusCode.Conflict)).ShouldBe("Fiscal.DeviceFinal");

        var accepted = await app.DocumentAsync(shop.Owner, encf);
        (accepted.Status, accepted.SecurityCode, accepted.TrackId, accepted.DgiiCode).ShouldBe((SubmissionStatus.Accepted, code, "TRACK-1", 1));
        EcfSubmissionQueue.Unzip(accepted.SignedXml).ShouldBe(signed); // the bytes the DGII got, kept for 10 years

        await using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(shop.Owner.TenantId);
        // The sale gets its security code and QR from the event, as one signed after a clock fix (TK-156).
        (await scope.ServiceProvider.GetRequiredService<InfraDbContext>().Outbox.CountAsync(m => m.Type.Contains("FiscalDocumentSignedV1"))).ShouldBe(1);
        // The cold archive, by the business's own node (what DeviceSignedArchiveWorker runs for every business).
        await scope.ServiceProvider.GetRequiredService<EcfSubmissionQueue>().ArchiveAsync(CancellationToken.None);
        app.Cloud.Requests.ShouldContain(r => r.Path == EcfSubmissionQueue.ArchivePath && r.Node == app.Cloud.Nodes["33333333"] && r.Body.GetProperty("encf").GetString() == encf);
        (await app.DocumentAsync(shop.Owner, encf)).ArchivedUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_32_signs_its_e_CF_then_its_RFCE_and_reports_the_DGII_answer()
    {
        var shop = await Shops.OpenAsync(app, "Dueño de la carnicería");
        var encf = await shop.SellAsync();
        var certificate = Shops.Certificate();

        var pending = await shop.PendingAsync(encf);
        (pending.Text("step"), pending.Text("channel")).ShouldBe(("Sign", "Rfce"));
        var (signed, code) = Shops.Sign(certificate, pending.Text("xml"));
        // An answer before the RFCE is signed has nothing behind it.
        (await shop.SubmitAsync(pending.GetProperty("id").GetGuid(), signed, answer: new { State = "Accepted" }).ProblemCodeAsync(HttpStatusCode.Conflict))
            .ShouldBe("Fiscal.DeviceNotReady");
        var rfce = await shop.SubmitAsync(pending.GetProperty("id").GetGuid(), signed).OkJsonAsync();
        (rfce.Text("step"), rfce.Text("status"), rfce.Text("securityCode")).ShouldBe(("SignRfce", "AwaitingDeviceSignature", code));
        rfce.Text("xml").ShouldContain($"<CodigoSeguridadeCF>{code}</CodigoSeguridadeCF>");

        var (signedRfce, _) = Shops.Sign(certificate, rfce.Text("xml"));
        var done = await shop.SubmitAsync(pending.GetProperty("id").GetGuid(), signedRfce: signedRfce, answer: new { State = "Accepted" }).OkJsonAsync();
        (done.Text("step"), done.Text("status")).ShouldBe(("Done", "Accepted"));
        var stored = await app.DocumentAsync(shop.Owner, encf);
        (EcfSubmissionQueue.Unzip(stored.SignedXml), EcfSubmissionQueue.Unzip(stored.RfceXml!)).ShouldBe((signed, signedRfce));
    }

    [Fact]
    public async Task A_document_changed_before_or_after_signing_is_refused_and_a_failure_waits_for_its_retry()
    {
        var shop = await Shops.OpenAsync(app, "Dueña de la panadería");
        var encf = await shop.SellAsync(buyerRnc: "101672919");
        var pending = await shop.PendingAsync(encf);
        var id = pending.GetProperty("id").GetGuid();
        var certificate = Shops.Certificate();

        // Signed, but not what the server built (another total): a valid signature over other content.
        var other = XDocument.Parse(pending.Text("xml"));
        other.Descendants("MontoTotal").First().Value = "1.00";
        (await shop.SubmitAsync(id, Shops.Sign(certificate, other.ToString(SaveOptions.DisableFormatting)).Xml).ProblemCodeAsync(HttpStatusCode.BadRequest))
            .ShouldBe("Fiscal.DeviceChanged");
        // Changed after signing: the signature no longer matches.
        var (signed, _) = Shops.Sign(certificate, pending.Text("xml"));
        (await shop.SubmitAsync(id, signed.Replace("Carne", "Cerdo")).ProblemCodeAsync(HttpStatusCode.BadRequest)).ShouldBe("Fiscal.DeviceSignature");
        // Not signed at all.
        (await shop.SubmitAsync(id, pending.Text("xml")).ProblemCodeAsync(HttpStatusCode.BadRequest)).ShouldBe("Fiscal.DeviceSignature");
        (await shop.SubmitAsync(id).ProblemCodeAsync(HttpStatusCode.BadRequest)).ShouldBe("Fiscal.DeviceSignatureRequired");
        var unchanged = await app.DocumentAsync(shop.Owner, encf);
        (unchanged.Status, unchanged.SecurityCode).ShouldBe((SubmissionStatus.AwaitingDeviceSignature, ""));

        // The device could not sign (its key is gone): offered again after the store's backoff.
        var failed = await shop.Client.PostAsJsonAsync($"/api/fiscal/device/documents/{id}/failure", new { Message = "El certificado no está en este equipo." })
            .OkJsonAsync();
        (failed.GetProperty("attempts").GetInt32(), failed.Text("lastError")).ShouldBe((1, "El certificado no está en este equipo."));
        (await shop.PendingAsync()).ShouldNotContain(d => d.Text("eNcf") == encf);
        app.Clock.Advance(EcfSubmissionQueue.RetryDelay(1));
        (await shop.PendingAsync(encf)).Text("step").ShouldBe("Sign");

        // Signed once, a second, different signature for it is refused (the DGII may hold the first).
        await shop.SubmitAsync(id, signed).OkJsonAsync();
        (await shop.SubmitAsync(id, Shops.Sign(Shops.Certificate(), pending.Text("xml")).Xml).ProblemCodeAsync(HttpStatusCode.Conflict))
            .ShouldBe("Fiscal.DeviceAlreadySigned");
    }

    [Fact]
    public async Task Only_the_businesss_RNC_or_its_signers_cedula_may_sign()
    {
        var shop = await Shops.OpenAsync(app, "Dueño de la ferretería");
        var encf = await shop.SellAsync(buyerRnc: "101672919");
        var pending = await shop.PendingAsync(encf);
        var id = pending.GetProperty("id").GetGuid();

        foreach (var serial in new[] { "101672919", null })
            (await shop.SubmitAsync(id, Shops.Sign(Shops.Certificate(serial), pending.Text("xml")).Xml).ProblemCodeAsync(HttpStatusCode.BadRequest))
                .ShouldBe("Fiscal.DeviceWrongRnc");
        // A person's certificate on the company's document: its delegated signer, as the store accepts at import (R8 #4).
        var signed = await shop.SubmitAsync(id, Shops.Sign(Shops.Certificate("00112345678"), pending.Text("xml")).Xml).OkJsonAsync();
        (signed.Text("step"), signed.Text("status")).ShouldBe(("Send", "Pending"));
    }

    [Fact]
    public async Task Each_business_has_its_own_environment_and_sees_only_its_own_documents()
    {
        var a = await Shops.OpenAsync(app, "Dueña de A");
        var b = await Shops.OpenAsync(app, "Dueño de B", rnc: "101672919", environment: "Simulated");
        var encfA = await a.SellAsync();
        var encfB = await b.SellAsync();

        // A's TesteCF is not B's: B still simulates (the store's stand-in), A waits for its device.
        (await a.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/mode")).Text("environment").ShouldBe("TesteCF");
        (await b.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/mode")).Text("environment").ShouldBe("Simulated");
        (await app.DocumentAsync(a.Owner, encfA)).Status.ShouldBe(SubmissionStatus.AwaitingDeviceSignature);
        (await app.DocumentAsync(b.Owner, encfB)).Status.ShouldBe(SubmissionStatus.Simulated);

        var idA = (await a.PendingAsync(encfA)).GetProperty("id").GetGuid();
        (await b.PendingAsync()).ShouldBeEmpty();
        var (signed, _) = Shops.Sign(Shops.Certificate(), (await a.PendingAsync(encfA)).Text("xml"));
        (await b.SubmitAsync(idA, signed).ProblemCodeAsync(HttpStatusCode.NotFound)).ShouldBe("Fiscal.DeviceNotFound");
        (await b.Client.PostAsJsonAsync($"/api/fiscal/device/documents/{idA}/failure", new { Message = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await a.PendingAsync(encfA)).GetProperty("attempts").GetInt32().ShouldBe(0);

        // The server's certificate store would be every business's: the .p12 stays on the device.
        (await a.Client.PostAsJsonAsync("/api/fiscal/certificate", new { File = "AAAA", Password = "x" }).ProblemCodeAsync(HttpStatusCode.Conflict))
            .ShouldBe("Fiscal.CertificateOnDevice");
        // An ANECF would be signed here, by no one: refused until the device signs it.
        (await a.Client.PostAsync($"/api/fiscal/documents/{idA}/void", null).ProblemCodeAsync(HttpStatusCode.Conflict)).ShouldBe("Fiscal.VoidOnDevice");
    }
}

/// <summary>TK-323: the 72 hours run from the moment the server built it; at 48 the device and the cloud's alert (heartbeat) warn. Own app: it moves the clock.</summary>
public sealed class DeviceSignedDeadlineTests(LiteApp app) : IClassFixture<LiteApp>
{
    [Fact]
    public async Task An_unsigned_document_warns_at_48_hours_and_is_overdue_after_72()
    {
        var shop = await Shops.OpenAsync(app, "Dueña de la farmacia");
        var soldUtc = app.Clock.GetUtcNow().UtcDateTime;
        var encf = await shop.SellAsync();

        app.Clock.Advance(TimeSpan.FromHours(47));
        shop = await shop.SignInAgainAsync(app);
        (await shop.PendingAsync(encf)).GetProperty("deadlineNear").GetBoolean().ShouldBeFalse();
        app.Clock.Advance(TimeSpan.FromHours(2));
        shop = await shop.SignInAgainAsync(app);
        (await shop.PendingAsync(encf)).GetProperty("deadlineNear").GetBoolean().ShouldBeTrue();

        // What the heartbeat sends the cloud, which alerts the owner (Alerts.QueueStale): it counts as not sent.
        await using (var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(shop.Owner.TenantId))
            (await scope.ServiceProvider.GetRequiredService<IFiscalReports>().HealthAsync(CancellationToken.None)).OldestPendingUtc
                .ShouldBe((await app.DocumentAsync(shop.Owner, encf)).SignedUtc);
        (await app.DocumentAsync(shop.Owner, encf)).SignedUtc.ShouldBe(soldUtc, TimeSpan.FromSeconds(5));

        app.Clock.Advance(TimeSpan.FromHours(24));
        shop = await shop.SignInAgainAsync(app);
        var queue = await shop.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/queue");
        (queue.GetProperty("pending").GetInt32(), queue.GetProperty("overdue").GetInt32()).ShouldBe((1, 1));
    }
}
