using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.Settings.Contracts;
using Toki.Sync.Contracts;

namespace Toki.Fiscal;

/// <summary>TK-153: delivering an accepted 31 to an electronic buyer. Pending → Delivered (ARECF Estado 0, or 1 "duplicate": it already has it) /
/// NotReceived (ARECF Estado 1 with another reason) / NotElectronic (not in the DGII directory: the buyer keeps the printed receipt) /
/// Failed (72 hours of failures).</summary>
internal enum DeliveryStatus { Pending, Delivered, NotReceived, NotElectronic, Failed }

internal sealed class EcfDelivery
{
    public Guid DocumentId { get; set; }
    public string ENcf { get; set; } = "";
    public string BuyerRnc { get; set; } = "";
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string? Message { get; set; }
    /// <summary>The buyer's signed acknowledgement (ARECF), gzip. Kept 10 years like the e-CF.</summary>
    public byte[]? AckXml { get; set; }
    public DateTime? DeliveredUtc { get; set; }
}

/// <summary>TK-154: an e-CF a supplier sent to this business, received and acknowledged by the cloud (TK-152) and pulled here.
/// The base to record the purchase (NCF, supplier, amounts) for the 606.</summary>
internal sealed class ReceivedDocument
{
    public Guid Id { get; set; }
    /// <summary>The cloud's id: the pull cursor.</summary>
    public long CloudId { get; set; }
    public string Environment { get; set; } = "";
    public string IssuerRnc { get; set; } = "";
    public string IssuerName { get; set; } = "";
    public string ENcf { get; set; } = "";
    public int Type { get; set; }
    public DateOnly IssuedOn { get; set; }
    public decimal Total { get; set; }
    public decimal Itbis { get; set; }
    public byte[] XmlGzip { get; set; } = [];
    public DateTime ReceivedUtc { get; set; }
    /// <summary>Approved or Rejected (commercial approval, optional), as the cloud signed it.</summary>
    public string? Approval { get; set; }
    public string? ApprovalReason { get; set; }
    public DateTime? ApprovalUtc { get; set; }
    public DateTime? ApprovalDeliveredUtc { get; set; }
    public DateTime? ApprovalDgiiUtc { get; set; }
    public string? ApprovalMessage { get; set; }
}

internal sealed class EcfExchangeConfiguration : IEntityTypeConfiguration<EcfDelivery>, IEntityTypeConfiguration<ReceivedDocument>
{
    public void Configure(EntityTypeBuilder<EcfDelivery> b)
    {
        b.ToTable("EcfDeliveries");
        b.HasKey(d => d.DocumentId);
        b.Property(d => d.ENcf).HasMaxLength(13);
        b.Property(d => d.BuyerRnc).HasMaxLength(11);
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.Message).HasMaxLength(1000);
        b.HasIndex(d => new { d.Status, d.NextAttemptUtc });
    }

    public void Configure(EntityTypeBuilder<ReceivedDocument> b)
    {
        b.ToTable("EcfReceived");
        b.HasIndex(d => d.CloudId).IsUnique();
        b.HasIndex(d => d.ReceivedUtc);
        b.Property(d => d.Environment).HasMaxLength(10);
        b.Property(d => d.IssuerRnc).HasMaxLength(11);
        b.Property(d => d.IssuerName).HasMaxLength(250);
        b.Property(d => d.ENcf).HasMaxLength(13);
        b.Property(d => d.Approval).HasMaxLength(10);
        b.Property(d => d.ApprovalReason).HasMaxLength(250);
        b.Property(d => d.ApprovalMessage).HasMaxLength(1000);
    }
}

internal static class EcfExchange
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ReceiverDirectoryCache>();
        services.AddScoped<EcfDeliveryQueue>();
        services.AddScoped<ReceivedDocumentsPuller>();
        services.AddHttpClient(EcfDeliveryQueue.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHostedService<EcfExchangeWorker>();
    }
}

/// <summary>Directory entries by RNC, kept a day (docs/ecf-propio.md TK-153: "caché diaria"). Null: not an electronic issuer.</summary>
internal sealed class ReceiverDirectoryCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    public ConcurrentDictionary<string, ((string? Reception, string? Approval, string? Authentication)? Entry, DateTimeOffset Fetched)> Entries { get; } = new();
}

/// <summary>TK-153: after the DGII accepts a 31 issued to an RNC, the store hands it to the buyer if the buyer is an electronic issuer: DGII
/// directory (cached a day) → the buyer's optional seed authentication → <c>POST</c> to its reception URL → its signed ARECF is kept.
/// Retries with the queue's backoff for 72 hours. Separate from the DGII queue: a buyer that is down never delays a submission.</summary>
internal sealed class EcfDeliveryQueue(FiscalDbContext db, IEcfProvider provider, IEcfSigner signer, ISettingsQueries settings, IHttpClientFactory http,
    ReceiverDirectoryCache directory, TimeProvider clock, ILogger<EcfDeliveryQueue> log)
{
    public const string HttpClientName = "Toki.EcfReceiver";
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(72);

    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        if (provider is not DgiiClient dgii) return 0; // simulated: nothing leaves the store
        var now = clock.GetUtcNow().UtcDateTime;
        var deliveries = db.Set<EcfDelivery>();
        // R9 #19: only 31 accepted within the delivery window. When the update that brings TK-153 arrives, the old ones are not handed out.
        var since = now - GiveUpAfter;
        var accepted = await db.Documents
            .Where(d => d.Type == EcfType.FiscalCredit31 && d.BuyerRnc != null && (d.Status == SubmissionStatus.Accepted || d.Status == SubmissionStatus.AcceptedConditional)
                && d.ResultUtc >= since && !deliveries.Any(x => x.DocumentId == d.Id))
            .Select(d => new { d.Id, d.ENcf, d.BuyerRnc }).Take(EcfSubmissionQueue.BatchSize).ToListAsync(ct);
        foreach (var a in accepted)
            deliveries.Add(new EcfDelivery { DocumentId = a.Id, ENcf = a.ENcf, BuyerRnc = a.BuyerRnc!, Status = DeliveryStatus.Pending, CreatedUtc = now });
        if (accepted.Count > 0) await db.SaveChangesAsync(ct);

        var due = await deliveries.Where(d => d.Status == DeliveryStatus.Pending && (d.NextAttemptUtc == null || d.NextAttemptUtc <= now))
            .OrderBy(d => d.CreatedUtc).Take(EcfSubmissionQueue.BatchSize).ToListAsync(ct);
        if (due.Count == 0) return 0;
        var issuerRnc = (await settings.GetBusinessProfileAsync(ct)).Rnc ?? "";
        foreach (var d in due)
        {
            try
            {
                var entry = await DirectoryAsync(dgii, d.BuyerRnc, ct);
                if (entry is not { Reception: { } reception })
                {
                    (d.Status, d.Message, d.NextAttemptUtc) = (DeliveryStatus.NotElectronic, "El comprador no es emisor electrónico: se queda con la representación impresa.", null);
                }
                else
                {
                    var xml = EcfSubmissionQueue.Unzip(await db.Documents.Where(x => x.Id == d.DocumentId).Select(x => x.SignedXml).SingleAsync(ct));
                    var ack = await PostAsync(reception, entry.Value.Authentication, $"{issuerRnc}{d.ENcf}.xml", xml, ct);
                    var (state, reason) = ReadAck(ack, d.ENcf);
                    d.AckXml = EcfSubmissionQueue.Zip(ack);
                    (d.Status, d.Message, d.NextAttemptUtc) = state == 0 || reason == 3
                        ? (DeliveryStatus.Delivered, reason == 3 ? "El comprador ya lo tenía (envío duplicado)." : null, (DateTime?)null)
                        : (DeliveryStatus.NotReceived, $"El comprador no lo recibió: {Reason(reason)}.", null);
                    if (d.Status == DeliveryStatus.Delivered) d.DeliveredUtc = now;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                d.Attempts++;
                var message = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
                (d.Status, d.NextAttemptUtc, d.Message) = now - d.CreatedUtc >= GiveUpAfter
                    ? (DeliveryStatus.Failed, (DateTime?)null, $"Se dejó de intentar después de 72 horas: {message}")
                    : (DeliveryStatus.Pending, now + EcfSubmissionQueue.RetryDelay(d.Attempts), message);
                log.LogWarning(ex, "Could not deliver e-CF {ENcf} to the buyer (attempt {Attempts})", d.ENcf, d.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
        return due.Count;
    }

    private async Task<(string? Reception, string? Approval, string? Authentication)?> DirectoryAsync(DgiiClient dgii, string rnc, CancellationToken ct)
    {
        if (directory.Entries.TryGetValue(rnc, out var cached) && clock.GetUtcNow() - cached.Fetched < ReceiverDirectoryCache.Lifetime) return cached.Entry;
        var entry = await dgii.DirectoryAsync(rnc, ct);
        directory.Entries[rnc] = (entry, clock.GetUtcNow());
        return entry;
    }

    /// <summary>Same exchange the DGII uses with a receiver: seed → signed seed → token, then the e-CF as multipart field <c>xml</c>. Returns the ARECF.</summary>
    private async Task<string> PostAsync(string reception, string? authentication, string fileName, string xml, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        using var request = Upload(ServiceUrl(reception, "recepcion/api/ecf"), fileName, xml);
        if (!string.IsNullOrWhiteSpace(authentication))
        {
            using var seed = await client.GetAsync(ServiceUrl(authentication, "autenticacion/api/semilla"), ct);
            seed.EnsureSuccessStatusCode();
            var seedXml = XDocument.Parse(await seed.Content.ReadAsStringAsync(ct));
            seedXml.Declaration = null;
            using var validate = Upload(ServiceUrl(authentication, "autenticacion/api/validacioncertificado"), "semilla.xml", signer.Sign(seedXml).SignedXml);
            using var token = await client.SendAsync(validate, ct);
            token.EnsureSuccessStatusCode();
            var json = await token.Content.ReadFromJsonAsync<JsonElement>(ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                json.TryGetProperty("token", out var t) ? t.GetString() : throw new HttpRequestException("El receptor no devolvió el token."));
        }
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"El receptor del comprador respondió {(int)response.StatusCode}.");
        return body;
    }

    /// <summary>The ARECF's Estado (0 received, 1 not) and reason. One that is not a signed ARECF for this e-NCF is a failure (retried).</summary>
    private static (int State, int? Reason) ReadAck(string ack, string encf)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(ack), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
            doc.Load(reader);
        }
        catch (XmlException) { throw new HttpRequestException("El receptor del comprador no devolvió un acuse de recibo (ARECF)."); }
        string? Text(string name) => doc.SelectSingleNode($"/ARECF/DetalleAcusedeRecibo/{name}")?.InnerText.Trim();
        if (Text("eNCF") != encf || !int.TryParse(Text("Estado"), out var state) || !SignatureIsValid(doc))
            throw new HttpRequestException("El acuse de recibo del comprador no es válido (e-NCF, estado o firma).");
        return (state, int.TryParse(Text("CodigoMotivoNoRecibido"), out var r) ? r : null);
    }

    private static bool SignatureIsValid(XmlDocument doc)
    {
        if (doc.DocumentElement!.ChildNodes.OfType<XmlElement>().LastOrDefault() is not { LocalName: "Signature" } last) return false;
        try
        {
            var signed = new SignedXml(doc);
            signed.LoadXml(last);
            var certificate = signed.KeyInfo.OfType<KeyInfoX509Data>().SelectMany(d => d.Certificates?.OfType<X509Certificate2>() ?? []).FirstOrDefault();
            return signed.SignedInfo is { References.Count: 1 } info && ((Reference)info.References[0]!).Uri == ""
                && certificate is not null && signed.CheckSignature(certificate, verifySignatureOnly: true);
        }
        catch (CryptographicException) { return false; }
    }

    private static string Reason(int? reason) => reason switch
    {
        1 => "error de especificación",
        2 => "error de firma digital",
        4 => "el RNC del comprador no corresponde",
        _ => "sin motivo",
    };

    /// <summary>The directory may list a base URL or the whole service URL: either way the service goes after <c>/fe/</c> (§11, question 31).</summary>
    internal static string ServiceUrl(string url, string service)
    {
        url = url.Trim().TrimEnd('/');
        var fe = url.IndexOf("/fe/", StringComparison.OrdinalIgnoreCase);
        return (fe >= 0 ? url[..fe] : url.EndsWith("/fe", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url) + "/fe/" + service;
    }

    private static HttpRequestMessage Upload(string url, string fileName, string xml)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = new MultipartFormDataContent { { file, "xml", fileName } } };
    }
}

/// <summary>TK-154: brings down what the cloud received for this business since the last one here. Not connected or cloud down: nothing, next time.</summary>
internal sealed class ReceivedDocumentsPuller(FiscalDbContext db, ICloudClient cloud, ILogger<ReceivedDocumentsPuller> log)
{
    public const string PullPath = "/fiscal/v1/received/pull";

    internal sealed record Page(IReadOnlyList<Item> Items);

    internal sealed record Item(long Id, string Environment, string IssuerRnc, string IssuerName, string ENcf, int Type, DateOnly IssuedOn, decimal Total,
        decimal Itbis, DateTime ReceivedUtc, string XmlGzip, string? Approval, string? ApprovalReason, DateTime? ApprovalUtc, DateTime? ApprovalDeliveredUtc,
        DateTime? ApprovalDgiiUtc, string? ApprovalMessage);

    public async Task<int> PullAsync(CancellationToken ct)
    {
        var received = db.Set<ReceivedDocument>();
        var after = await received.MaxAsync(d => (long?)d.CloudId, ct) ?? 0;
        Page? page;
        try
        {
            using var response = await cloud.PostAsJsonAsync(PullPath, new { after }, ct);
            if (!response.IsSuccessStatusCode) return 0;
            page = await response.Content.ReadFromJsonAsync<Page>(ct);
        }
        catch (InvalidOperationException) { return 0; } // not connected to the cloud
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not pull the received e-CF from the cloud");
            return 0;
        }
        foreach (var i in page?.Items ?? [])
            received.Add(new ReceivedDocument
            {
                Id = Guid.CreateVersion7(), CloudId = i.Id, Environment = i.Environment, IssuerRnc = i.IssuerRnc, IssuerName = i.IssuerName, ENcf = i.ENcf,
                Type = i.Type, IssuedOn = i.IssuedOn, Total = i.Total, Itbis = i.Itbis, XmlGzip = Convert.FromBase64String(i.XmlGzip), ReceivedUtc = i.ReceivedUtc,
                Approval = i.Approval, ApprovalReason = i.ApprovalReason, ApprovalUtc = i.ApprovalUtc, ApprovalDeliveredUtc = i.ApprovalDeliveredUtc,
                ApprovalDgiiUtc = i.ApprovalDgiiUtc, ApprovalMessage = i.ApprovalMessage,
            });
        await db.SaveChangesAsync(ct);
        return page?.Items.Count ?? 0;
    }
}

/// <summary>Every <c>Fiscal:SubmitIntervalSeconds</c> (30) when <c>Fiscal:AutoSubmit</c> is true: the 31 deliveries and the pull of received e-CF.</summary>
internal sealed class EcfExchangeWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<EcfExchangeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue<bool>("Fiscal:AutoSubmit")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue("Fiscal:SubmitIntervalSeconds", 30)), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ReceivedDocumentsPuller>().PullAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<EcfDeliveryQueue>().ProcessAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "The e-CF exchange (deliveries, received documents) failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
