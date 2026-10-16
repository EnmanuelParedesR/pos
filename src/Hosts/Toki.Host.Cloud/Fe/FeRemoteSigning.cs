using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Fe;

/// <summary>TK-246: signatures the cloud asks of a store's main PC (<see cref="FeSigningModes.StoreSigned"/>). The receiver puts a request in the node's
/// queue and waits up to <c>Fe:SignTimeoutSeconds</c> (10); the node holds a long poll (<c>POST /fiscal/v1/receiver/sign-requests/next</c>, signed as
/// the node) and posts the signed XML back. A node not seen polling for a minute is taken as offline and nothing waits for it.</summary>
/// <remarks>shortcut: in memory, one App Service instance (as <see cref="NodeNonces"/>): the node's poll and the supplier's request must reach the same
/// instance. Move the queue to the database or a cache service before scaling out.</remarks>
internal sealed class FeSignBroker(IConfiguration config)
{
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(1);

    public sealed record Request(Guid Id, Guid NodeId, string Xml)
    {
        internal TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<Guid, Request> _pending = new();
    private readonly ConcurrentDictionary<Guid, Channel<Request>> _queues = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastPoll = new();

    public TimeSpan Timeout => TimeSpan.FromSeconds(config.GetValue("Fe:SignTimeoutSeconds", 10));
    public TimeSpan PollWait => TimeSpan.FromSeconds(config.GetValue("Fe:SignPollSeconds", 25));

    /// <summary>The node polled within <see cref="OfflineAfter"/>.</summary>
    public bool IsOnline(Guid nodeId) => _lastPoll.TryGetValue(nodeId, out var seen) && DateTime.UtcNow - seen <= OfflineAfter;

    /// <summary>The signed XML, or null when the node is offline, did not answer in time or refused.</summary>
    public async Task<string?> SignAsync(Guid nodeId, string xml, CancellationToken ct)
    {
        if (!IsOnline(nodeId)) return null;
        var request = new Request(Guid.NewGuid(), nodeId, xml);
        _pending[request.Id] = request;
        try
        {
            Queue(nodeId).Writer.TryWrite(request);
            return await request.Result.Task.WaitAsync(Timeout, ct);
        }
        catch (TimeoutException) { return null; }
        finally { _pending.TryRemove(request.Id, out _); }
    }

    /// <summary>The next request for the node, waiting up to <see cref="PollWait"/>; null when none came.</summary>
    public async Task<Request?> NextAsync(Guid nodeId, CancellationToken ct)
    {
        _lastPoll[nodeId] = DateTime.UtcNow;
        var reader = Queue(nodeId).Reader;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(PollWait);
        try
        {
            while (await reader.WaitToReadAsync(wait.Token))
                while (reader.TryRead(out var r))
                    if (_pending.ContainsKey(r.Id)) return r; // the others timed out already
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally { _lastPoll[nodeId] = DateTime.UtcNow; }
        return null;
    }

    /// <summary>The node's answer: the signed XML, or null when it refused. False when the request is not this node's or no longer waits.</summary>
    public bool Complete(Guid nodeId, Guid id, string? signedXml) =>
        _pending.TryGetValue(id, out var r) && r.NodeId == nodeId && r.Result.TrySetResult(signedXml);

    /// <remarks>Bounded: a flood while the node is slow drops the oldest request (it would time out anyway).</remarks>
    private Channel<Request> Queue(Guid nodeId) => _queues.GetOrAdd(nodeId, _ =>
        Channel.CreateBounded<Request>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest }));
}

/// <summary>TK-246: what the cloud accepts back from the store. The very document it asked for, enveloped XMLDSig over the whole of it (only the
/// enveloped and C14N transforms), by a certificate of an INDOTEL authority that belongs to the receiver's RNC, or (R11 B-High2) is the very
/// certificate the receiver was activated with (a delegated cédula Toki support approved). Anything else is no signature.</summary>
internal static class FeStoreSignature
{
    public static bool Matches(string signed, XDocument requested, FeCertificate receiver, FeTrust trust)
    {
        var doc = FeXml.Parse(signed);
        if (doc is null) return false;
        if (!trust.SignedByTrusted(doc, receiver.Rnc)
            && !(FeXml.Signer(doc, out var carried) is { } signer && signer.Thumbprint == receiver.Thumbprint && trust.IsTrusted(signer, carried)))
            return false;
        try
        {
            var signature = new SignedXml(doc);
            signature.LoadXml((XmlElement)doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
            var chain = ((Reference)signature.SignedInfo!.References[0]!).TransformChain;
            for (var i = 0; i < chain.Count; i++)
                if (chain[i] is not (XmlDsigEnvelopedSignatureTransform or XmlDsigC14NTransform)) return false;
            var content = XDocument.Parse(signed);
            content.Root!.Elements(XName.Get("Signature", SignedXml.XmlDsigNamespaceUrl)).Remove();
            return XNode.DeepEquals(content.Root, requested.Root);
        }
        catch (Exception e) when (e is CryptographicException or XmlException) { return false; }
    }
}

/// <summary>TK-246 (council, mitigation b): what arrived while the store was off is acknowledged when it is back. The ARECF is signed and kept (with
/// the received document, in <see cref="FeSignature"/>); it is not sent to the supplier until the DGII says a late acknowledgement is allowed
/// (docs/ecf-propio.md §11, question 44). The supplier's own retry, if it comes, gets it then.</summary>
internal sealed class FeLateAcknowledger(CloudDbContext db, FeSigner signer, FeSignBroker broker, TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var done = 0;
        var due = await db.FeReceived.Where(d => d.AcknowledgedUtc == null).OrderBy(d => d.Id).Take(50).ToListAsync(ct);
        foreach (var d in due)
        {
            var receiver = await db.FeCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == d.TenantId && c.RevokedUtc == null, ct);
            if (receiver is null || receiver.SigningMode == FeSigningModes.StoreSigned && !broker.IsOnline(receiver.ConsentedByNodeId)) continue;
            d.AcknowledgedUtc = clock.GetUtcNow().UtcDateTime;
            if (await FeReceiverEndpoints.AcknowledgeAsync(signer, receiver, d.IssuerRnc, d.BuyerRnc, d.ENcf, null, clock, ct) is not null) done++;
            else
            {
                d.AcknowledgedUtc = null;
                await db.SaveChangesAsync(ct);
            }
        }
        return done;
    }
}

/// <summary>Runs <see cref="FeLateAcknowledger"/> every 30 s unless <c>Fe:LateAcksInBackground</c> is false (tests call it).</summary>
internal sealed class FeLateAcknowledgerWorker(IServiceScopeFactory scopes, ILogger<FeLateAcknowledgerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FeLateAcknowledger>().RunAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogError(e, "Late e-CF acknowledgements failed"); }
        }
    }
}

/// <param name="Proof">An <c>ActivacionReceptor</c> (RNC + the cloud's challenge) the store signed with its certificate: the proof that the RNC is its.</param>
public sealed record FeActivationRequest(string? Proof, string? Rnc, string? OwnerName, string? HolderName, bool Accepted, string? ConsentVersion);

/// <param name="SignedXml">The signed document, or null with <paramref name="Refused"/> when the store would not sign it.</param>
public sealed record FeSignAnswer(string? SignedXml, string? Refused);

internal static class FeRemoteSigningEndpoints
{
    public const string ActivationRoot = "ActivacionReceptor";
    private const string ActivationPurpose = "activation";

    public static void MapFeRemoteSigning(this RouteGroupBuilder store)
    {
        // The challenge the store signs to prove the RNC (5 minutes, this tenant only).
        store.MapPost("/receiver/challenge", (ClaimsPrincipal user, FeTokens tokens) =>
            Results.Ok(new { challenge = tokens.NewSeed(NodeIdentity.From(user).TenantId, ActivationPurpose) }));

        // StoreSigned: the same R9 #6 checks as the vault copy, on the proof's signature instead of an uploaded .p12.
        store.MapPost("/receiver/activate", async (FeActivationRequest r, ClaimsPrincipal user, HttpRequest http, CloudDbContext db, IFeCertificateVault vault,
            FeTrust trust, FeTokens tokens, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            var (owner, holder, rnc) = (r.OwnerName?.Trim() ?? "", r.HolderName?.Trim() ?? "", r.Rnc?.Trim() ?? "");
            if (!r.Accepted || r.ConsentVersion != FeConsent.StoreSignedVersion || owner.Length is 0 or > 150 || holder.Length is 0 or > 150)
                return EndpointExtensions.Problem(FeErrors.ConsentRequired);
            if (!Rnc.IsValid(rnc)) return EndpointExtensions.Problem(FeErrors.InvalidRnc);
            var proof = r.Proof is { } p ? FeXml.Parse(p) : null;
            if (proof?.DocumentElement?.Name != ActivationRoot || FeXml.Text(proof, $"/{ActivationRoot}/RNC") != rnc
                || FeXml.Text(proof, $"/{ActivationRoot}/Reto") is not { } challenge || !tokens.SeedIsValid(challenge, node.TenantId, ActivationPurpose)
                || FeXml.Signer(proof, out var carried) is not { } signer)
                return EndpointExtensions.Problem(FeErrors.ProofInvalid);
            var now = clock.GetUtcNow().UtcDateTime;
            if (signer.NotAfter.ToUniversalTime() <= now) return EndpointExtensions.Problem(FeErrors.CertificateExpired);
            if (!trust.IsTrusted(signer, carried)) return EndpointExtensions.Problem(FeErrors.CertificateUntrusted);
            if (await FeDelegations.CheckAsync(db, node, rnc, signer, holder, now, ct) is { } refused) return refused;
            var row = new FeCertificate
            {
                Id = Guid.CreateVersion7(), TenantId = node.TenantId, Rnc = rnc, Subject = FeXml.Cut(signer.Subject, 500), Thumbprint = signer.Thumbprint,
                NotAfterUtc = signer.NotAfter.ToUniversalTime(), SigningMode = FeSigningModes.StoreSigned, OwnerName = owner, HolderName = holder,
                ConsentVersion = FeConsent.StoreSignedVersion, ConsentedByNodeId = node.NodeId, ConsentedUtc = now,
            };
            return await FeServices.ActivateAsync(db, vault, row, http, config, ct);
        });

        // The node's long poll: 200 with the next request, 204 when none came, 404 when this PC is not the one that signs for the receiver.
        store.MapPost("/receiver/sign-requests/next", async (ClaimsPrincipal user, CloudDbContext db, FeSignBroker broker, TimeProvider clock, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            if (!await db.FeCertificates.AnyAsync(c => c.TenantId == node.TenantId && c.RevokedUtc == null && c.SigningMode == FeSigningModes.StoreSigned
                    && c.ConsentedByNodeId == node.NodeId, ct))
                return EndpointExtensions.Problem(Error.NotFound("Fe.NotSigningNode", "Esta PC no firma para el receptor de comprobantes."));
            var now = clock.GetUtcNow().UtcDateTime;
            await db.FeReceiverOutages.Where(o => o.TenantId == node.TenantId && o.NodeId == node.NodeId && o.ToUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ToUtc, now), ct);
            return await broker.NextAsync(node.NodeId, ct) is { } next ? Results.Ok(new { id = next.Id, xml = next.Xml }) : Results.NoContent();
        });

        store.MapPost("/receiver/sign-requests/{id:guid}", (Guid id, FeSignAnswer a, ClaimsPrincipal user, FeSignBroker broker, ILoggerFactory logs) =>
        {
            var node = NodeIdentity.From(user).NodeId;
            if (a.SignedXml is null)
                logs.CreateLogger("Toki.Fe").LogWarning("Node {NodeId} refused to sign request {RequestId}: {Reason}", node, id, a.Refused);
            return broker.Complete(node, id, a.SignedXml) ? Results.NoContent()
                : EndpointExtensions.Problem(Error.NotFound("Fe.SignRequestGone", "Esa solicitud de firma ya no espera respuesta."));
        });
    }
}
