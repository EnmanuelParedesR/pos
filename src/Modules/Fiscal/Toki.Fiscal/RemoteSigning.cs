using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.Settings.Contracts;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.Fiscal;

/// <summary>TK-246: the store signs for its receiver in the cloud (StoreSigned mode): the certificate never leaves this PC. A long poll as the node
/// (<c>POST /fiscal/v1/receiver/sign-requests/next</c>, signed and with the body hashed, ADR-0008) brings what the cloud needs signed; the answer
/// goes back the same way. <b>Only</b> acknowledgements (ARECF) are signed on the cloud's request, and only for this business: an ACECF or a seed
/// is signed here when the store itself sends it, never because the cloud asked (a signed seed is a DGII token for the RNC). Every request,
/// signed or refused, goes to the audit log.</summary>
internal sealed class RemoteSigning(ICloudClient cloud, CertificateEcfSigner signer, ISettingsQueries settings, ICommandHandler<RemoteSigning.Recorded, Ok> record,
    TimeProvider clock, ILogger<RemoteSigning> log)
{
    public const string NextPath = "/fiscal/v1/receiver/sign-requests/next";
    public static readonly IReadOnlySet<string> AllowedRoots = new HashSet<string>(StringComparer.Ordinal) { "ARECF" };
    /// <summary>Not connected, no certificate, or the cloud failed: asked again after this.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(1);
    /// <summary>The cloud says this PC does not sign for the receiver (404 <c>Fe.NotSigningNode</c>: not activated, or another PC activated it).
    /// Polling every minute would keep the serverless database awake for nothing; activating here wakes the poll at once
    /// (<see cref="RemoteSigningWake"/>).</summary>
    public static readonly TimeSpan NotSigning = TimeSpan.FromHours(6);

    internal sealed record SignRequest(Guid Id, string Xml);

    internal sealed record Recorded(Guid RequestId, string? Root, string? ENcf, string? IssuerRnc, string? Sha256, string? Refused) : ICommand<Ok>;

    internal sealed class Handler(IAuditLog audit) : ICommandHandler<Recorded, Ok>
    {
        public async Task<Result<Ok>> Handle(Recorded c, CancellationToken ct)
        {
            await audit.RecordAsync(c.Refused is null ? "fiscal.remote-sign" : "fiscal.remote-sign-refused", "RemoteSignature", c.RequestId,
                new { c.Root, c.ENcf, c.IssuerRnc, c.Sha256, c.Refused }, ct);
            return Ok.Value;
        }
    }

    /// <summary>One poll: answers the request that came, if any. Returns how long to wait before the next one.</summary>
    public async Task<TimeSpan> PollOnceAsync(CancellationToken ct)
    {
        if (signer.CannotSign(clock.GetUtcNow().UtcDateTime) is not null) return Idle;
        HttpResponseMessage response;
        try { response = await cloud.PostAsJsonAsync(NextPath, new { }, ct); }
        catch (InvalidOperationException) { return Idle; } // not connected to the cloud
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent) return TimeSpan.Zero;
            if (response.StatusCode == HttpStatusCode.NotFound) return NotSigning;
            if (!response.IsSuccessStatusCode || await response.Content.ReadFromJsonAsync<SignRequest>(ct) is not { } request) return Idle;
            var (signed, refused) = await SignAsync(request.Id, request.Xml, ct);
            using var answer = await cloud.PostAsJsonAsync($"/fiscal/v1/receiver/sign-requests/{request.Id}", new { signedXml = signed, refused }, ct);
            if (!answer.IsSuccessStatusCode) log.LogWarning("The cloud did not take the signature of request {RequestId} ({Status})", request.Id, (int)answer.StatusCode);
            return TimeSpan.Zero;
        }
    }

    /// <summary>The signed ARECF, or why it was refused. Recorded either way.</summary>
    internal async Task<(string? Signed, string? Refused)> SignAsync(Guid requestId, string xml, CancellationToken ct)
    {
        var doc = Parse(xml);
        var rnc = (await settings.GetBusinessProfileAsync(ct)).Rnc;
        string? Text(string name) => doc?.Root?.Element("DetalleAcusedeRecibo")?.Element(name)?.Value.Trim();
        var refused = doc?.Root is not { } root ? "No es un XML válido."
            : root.Name.Namespace != XNamespace.None || !AllowedRoots.Contains(root.Name.LocalName) ? $"La tienda no firma un {root.Name.LocalName} a pedido de la nube."
            : EcfXml.Validate(doc, EcfXml.ArecfSchema) is [var error, ..] ? $"El acuse no cumple el formato de la DGII: {error}"
            // A refusal (Estado 1) may name another buyer (motivo 4: the e-CF was not for this RNC); a receipt is only ever for this business.
            : Text("Estado") == "0" && Text("RNCComprador") != rnc ? "El acuse no es de este negocio (RNC del comprador)."
            : null;
        var signed = refused is null ? signer.Sign(doc!).SignedXml : null;
        await record.Handle(new Recorded(requestId, doc?.Root?.Name.LocalName, Text("eNCF"), Text("RNCEmisor"),
            signed is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signed))), refused), ct);
        if (refused is not null) log.LogWarning("Refused to sign request {RequestId} of the cloud: {Reason}", requestId, refused);
        return (signed, refused);
    }

    private static XDocument? Parse(string xml)
    {
        if (xml.Length > 1_000_000) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (XmlException) { return null; }
    }
}

/// <summary>Keeps the long poll open while the store is connected, unless <c>Sync:Enabled</c> is false (the test host calls
/// <see cref="RemoteSigning.PollOnceAsync"/>).</summary>
internal sealed class RemoteSigningWorker(IServiceScopeFactory scopes, IConfiguration config, RemoteSigningWake wake, TimeProvider clock, ILogger<RemoteSigningWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Sync:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            var woken = wake.Next; // read before polling: an activation during the poll still cuts the wait short
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                wait = await scope.ServiceProvider.GetRequiredService<RemoteSigning>().PollOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !stoppingToken.IsCancellationRequested)
            {
                wait = TimeSpan.FromSeconds(30); // no internet or the cloud is down: the receiver answers 503 meanwhile
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Remote signing for the cloud receiver failed");
                wait = RemoteSigning.Idle;
            }
            if (wait <= TimeSpan.Zero) continue;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await Task.WhenAny(Task.Delay(wait, clock, waiting.Token), woken);
            await waiting.CancelAsync(); // the delay a wake cut short
        }
    }
}

/// <summary>Cuts the remote signing poll's wait short: the receiver was just activated on this PC (StoreSigned), so the cloud now has requests for it.</summary>
internal sealed class RemoteSigningWake
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when <see cref="Now"/> is called after this was read.</summary>
    public Task Next => Volatile.Read(ref _next).Task;

    public void Now() => Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}
