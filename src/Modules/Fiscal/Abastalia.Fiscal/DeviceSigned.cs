using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal;

/// <summary>TK-323 (ADR-0013 decision 2): <c>Fiscal:Signer</c> = <c>Device</c>, the Lite host. No certificate on the server: each business's own
/// device signs, sends to the DGII and reports back (<see cref="DeviceSigned"/>). The environment is the business's own (its <c>fiscal.Mode</c>
/// row, under the tenant filter and RLS), never the process's <see cref="FiscalMode"/>, which is one for every business.</summary>
internal sealed class DeviceSigning(IConfiguration config, FiscalDbContext db)
{
    private FiscalEnvironment? _environment;

    public bool Enabled { get; } = config["Fiscal:Signer"] == "Device";

    /// <summary>Read once per scope (a request, a worker pass), so it is synchronous: <see cref="FiscalDocuments.Environment"/> is a property.</summary>
    public FiscalEnvironment Environment => _environment ??=
        db.Set<FiscalModeSetting>().AsNoTracking().Select(m => (FiscalEnvironment?)m.Environment).FirstOrDefault() ?? FiscalEnvironment.Simulated;
}

/// <summary>TK-323: what the server checks in an XML its device signed, before it keeps it. The rules of <c>FeXml.Signer</c> in the cloud (one
/// enveloped XMLDSig, last child of the root, RSA-SHA256, one <c>Reference URI=""</c> with a SHA-256 digest, valid with a certificate it carries),
/// plus: without its signature it is exactly the XML the server built (canonical bytes), and the signed digest is that XML's.</summary>
/// <remarks>shortcut: a copy of the cloud's checks, as <c>FeXml.SignAsync</c> copies the store's signer; the cloud references only module contracts.</remarks>
internal static class DeviceSignature
{
    public const int MaxChars = 4_000_000;

    public sealed record Checked(X509Certificate2 Certificate, string SecurityCode);

    /// <returns>The certificate and the security code (first 6 characters of SignatureValue), or why it is refused.</returns>
    public static Result<Checked> Verify(string signedXml, XDocument built)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(signedXml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxChars });
            doc.Load(reader);
        }
        catch (XmlException) { return DeviceSignedErrors.Invalid("no es un XML válido"); }
        if (doc.DocumentElement is not { } root) return DeviceSignedErrors.Invalid("no es un XML válido");

        var signatures = doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        if (signatures.Count != 1 || !ReferenceEquals(root.ChildNodes.OfType<XmlElement>().LastOrDefault(), signatures[0]))
            return DeviceSignedErrors.Invalid("debe tener una sola firma, al final del documento");
        var element = (XmlElement)signatures[0]!;
        X509Certificate2? certificate;
        Reference reference;
        string code;
        try
        {
            var signed = new SignedXml(doc);
            signed.LoadXml(element);
            if (signed.SignedInfo is not { References.Count: 1 } info || info.SignatureMethod != SignedXml.XmlDsigRSASHA256Url
                || (reference = (Reference)info.References[0]!).Uri != "" || reference.DigestMethod != SignedXml.XmlDsigSHA256Url)
                return DeviceSignedErrors.Invalid("la firma no es la de la DGII (RSA-SHA256 sobre todo el documento)");
            certificate = signed.KeyInfo.OfType<KeyInfoX509Data>().SelectMany(d => d.Certificates?.OfType<X509Certificate2>() ?? [])
                .FirstOrDefault(c => signed.CheckSignature(c, verifySignatureOnly: true));
            if (certificate is null) return DeviceSignedErrors.Invalid("la firma no es válida");
            code = Convert.ToBase64String(signed.SignatureValue!)[..6];
        }
        catch (CryptographicException) { return DeviceSignedErrors.Invalid("la firma no es válida"); }

        // Nothing changed: the document without its signature is, canonical, byte for byte the one built, and the signed digest is its digest.
        var expected = Canonical(Load(built));
        root.RemoveChild(element);
        if (!expected.AsSpan().SequenceEqual(Canonical(doc)) || !SHA256.HashData(expected).AsSpan().SequenceEqual(reference.DigestValue))
            return DeviceSignedErrors.Changed;
        return new Checked(certificate, code);
    }

    /// <summary>As <see cref="CertificateEcfSigner"/> loads what it signs (whitespace not preserved).</summary>
    private static XmlDocument Load(XDocument xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
        using var reader = xml.CreateReader();
        doc.Load(reader);
        return doc;
    }

    /// <summary>C14N 1.0 without comments: what the enveloped signature's digest covers.</summary>
    private static byte[] Canonical(XmlDocument doc)
    {
        var c14n = new XmlDsigC14NTransform();
        c14n.LoadInput(doc);
        using var stream = (Stream)c14n.GetOutput(typeof(Stream));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}

internal static class DeviceSignedErrors
{
    public static readonly Error NotFound = Error.NotFound("Fiscal.DeviceNotFound", "Ese comprobante no espera nada de este equipo.");
    public static readonly Error SignatureRequired = Error.Validation("Fiscal.DeviceSignatureRequired", "Falta el comprobante firmado.");
    public static readonly Error AlreadySigned = Error.Conflict("Fiscal.DeviceAlreadySigned", "Ese comprobante ya se firmó con otra firma.");
    public static readonly Error NotSent = Error.Conflict("Fiscal.DeviceNotReady", "Firme el comprobante antes de enviar la respuesta de la DGII.");
    public static readonly Error Final = Error.Conflict("Fiscal.DeviceFinal", "La DGII ya respondió por ese comprobante.");

    public static readonly Error Changed = Error.Validation("Fiscal.DeviceChanged",
        "El comprobante firmado no es el que armó el servidor: se cambió algo antes de firmar. No se guardó.");

    public static Error Invalid(string why) => Error.Validation("Fiscal.DeviceSignature", $"El comprobante firmado no sirve: {why}.");

    public static Error WrongRnc(string? certificate, string? business) => Error.Validation("Fiscal.DeviceWrongRnc",
        $"El certificado que firmó es de {certificate ?? "nadie (no trae RNC ni cédula)"} y el negocio es {business}: la DGII lo rechazaría. Importe en este equipo el certificado del negocio.");

    public static readonly Error CertificateExpired = Error.Validation("Fiscal.DeviceCertificateExpired",
        "El certificado que firmó no está vigente. Importe en este equipo uno vigente.");
}

/// <summary>TK-323 (ADR-0013, flow of a sale): the sale reserved the e-NCF and built the XML unsigned (<see cref="SubmissionStatus.AwaitingDeviceSignature"/>);
/// the business's device takes it from here, as the store's queue does on the main PC (<see cref="EcfSubmissionQueue"/>):
/// <list type="bullet">
/// <item><c>GET /api/fiscal/device/documents</c>: what it has to do, closest 72-hour deadline first: <see cref="Step.Sign"/> the e-CF,
/// <see cref="Step.SignRfce"/> (a 32 under RD$250,000: the summary carries the e-CF's security code, so it comes after), <see cref="Step.Send"/>
/// (signed, not at the DGII yet) or <see cref="Step.Poll"/> (TrackId or RFCE "en proceso").</item>
/// <item><c>POST /api/fiscal/device/documents/{id}</c>: the signed XML, the signed RFCE and/or the DGII's answer, in that order, each optional;
/// the answer is the next step. Every signature is checked (<see cref="DeviceSignature"/>, the certificate's RNC or the signer's cédula as
/// <see cref="IEcfCertificateHolder.Fits"/>) and the sale gets its code and QR (<see cref="FiscalDocumentSignedV1"/>).</item>
/// <item><c>POST /api/fiscal/device/documents/{id}/failure</c>: it could not sign or reach the DGII; offered again after the store's backoff.</item>
/// </list>
/// Not in the Lite host (<c>Fiscal:Signer</c> other than <c>Device</c>) every route is 404.</summary>
/// <remarks>shortcut: the DGII's answer is the device's word (unsigned JSON, the server never calls the DGII); TK-328 can compare it with the
/// DGII's own lookup once the server has a way to ask.</remarks>
internal static class DeviceSigned
{
    /// <summary>Owner warned from here on (ADR-0013: 48 h of the 72); the cloud's queue alert comes from the heartbeat at 24 h.</summary>
    public static readonly TimeSpan WarnAfter = TimeSpan.FromHours(48);

    public enum Step { Sign, SignRfce, Send, Poll, Done }

    /// <param name="Xml">Sign / SignRfce: the exact XML to sign. Send: the signed e-CF, or the signed RFCE for that channel.</param>
    /// <param name="DeadlineNear">48 hours or more since it was built: the device warns the owner.</param>
    public sealed record Item(Guid Id, string ENcf, EcfType Type, EcfChannel Channel, FiscalEnvironment Environment, SubmissionStatus Status, Step Step,
        string? Xml, string? SecurityCode, string? TrackId, DateTime SubmitDeadlineUtc, bool DeadlineNear, int Attempts, string? LastError);

    /// <summary>What the DGII said, as the browser's <c>dgiiClient</c> returns it.</summary>
    public sealed record Answer(DgiiState State, string? TrackId = null, bool? SequenceUsed = null, string? Messages = null);

    public sealed record Body(string? SignedXml = null, string? SignedRfce = null, Answer? Answer = null);

    public sealed record Submit(Guid Id, string? SignedXml, string? SignedRfce, Answer? Answer) : ICommand<Item>;

    public sealed record FailureBody(string Message);

    public sealed record Failure(Guid Id, string Message) : ICommand<Item>;

    public static Item ItemOf(FiscalDocument d, DateTime now)
    {
        var (step, xml) = d.Status switch
        {
            SubmissionStatus.AwaitingDeviceSignature when d.AwaitingSignature => (Step.Sign, EcfSubmissionQueue.Unzip(d.SignedXml)),
            SubmissionStatus.AwaitingDeviceSignature => (Step.SignRfce, Rfce(d).ToString(SaveOptions.DisableFormatting)),
            SubmissionStatus.Pending => (Step.Send, EcfSubmissionQueue.Unzip(d.Channel == EcfChannel.Rfce ? d.RfceXml! : d.SignedXml)),
            SubmissionStatus.Sent => (Step.Poll, null),
            _ => (Step.Done, (string?)null),
        };
        return new Item(d.Id, d.ENcf, d.Type, d.Channel, d.Environment, d.Status, step, xml, d.AwaitingSignature ? null : d.SecurityCode, d.TrackId,
            d.SubmitDeadlineUtc, step != Step.Done && now >= d.SubmitDeadlineUtc - FiscalDocuments.SubmitDeadline + WarnAfter, d.Attempts, d.LastError);
    }

    /// <summary>The RFCE of a signed 32, as the store builds it (<see cref="FiscalDocuments"/>): the same input gives the same bytes.</summary>
    private static XDocument Rfce(FiscalDocument d) => EcfXml.Rfce(EcfSubmissionQueue.Unzip(d.SignedXml), d.SecurityCode);

    internal sealed class SubmitValidator : AbstractValidator<Submit>
    {
        public SubmitValidator()
        {
            RuleFor(c => c.SignedXml!.Length).LessThanOrEqualTo(DeviceSignature.MaxChars).When(c => c.SignedXml is not null);
            RuleFor(c => c.SignedRfce!.Length).LessThanOrEqualTo(DeviceSignature.MaxChars).When(c => c.SignedRfce is not null);
            RuleFor(c => c.Answer!.State).IsInEnum().NotEqual(DgiiState.Simulated).When(c => c.Answer is not null);
            RuleFor(c => c.Answer!.TrackId).MaximumLength(100).When(c => c.Answer is not null);
        }
    }

    internal sealed class SubmitHandler(FiscalDbContext db, DeviceSigning device, ISettingsQueries settings, IOutbox outbox, TimeProvider clock,
        ILogger<SubmitHandler> log) : ICommandHandler<Submit, Item>
    {
        public async Task<Result<Item>> Handle(Submit c, CancellationToken ct)
        {
            var d = await db.Documents.SingleOrDefaultAsync(x => x.Id == c.Id, ct);
            if (!device.Enabled || d is null || d.Environment == FiscalEnvironment.Simulated) return DeviceSignedErrors.NotFound;
            var now = clock.GetUtcNow().UtcDateTime;
            var business = await settings.GetBusinessProfileAsync(ct);

            if (d.Status == SubmissionStatus.AwaitingDeviceSignature && d.AwaitingSignature)
            {
                if (c.SignedXml is null) return DeviceSignedErrors.SignatureRequired;
                var built = XDocument.Parse(EcfSubmissionQueue.Unzip(d.SignedXml));
                var ecf = Check(d, c.SignedXml, built, business, now);
                if (ecf.IsFailure) return ecf.Error!;
                (d.SignedXml, d.SecurityCode) = (EcfSubmissionQueue.Zip(c.SignedXml), ecf.Value.SecurityCode);
                if (d.Channel == EcfChannel.Rfce && EcfXml.ValidateUnsigned(Rfce(d), EcfXml.RfceSchema) is [var error, ..]) return FiscalErrors.InvalidXml(error);
                if (d.Channel == EcfChannel.Ecf) d.Status = SubmissionStatus.Pending;
                (d.LastError, d.NextAttemptUtc) = (null, null);
                outbox.Add(new FiscalDocumentSignedV1(d.Id, d.ENcf, d.ReferenceId, d.SecurityCode, FiscalDocuments.Qr(d, business, built), now));
            }
            else if (c.SignedXml is not null && c.SignedXml != EcfSubmissionQueue.Unzip(d.SignedXml)) return DeviceSignedErrors.AlreadySigned;

            if (d.Status == SubmissionStatus.AwaitingDeviceSignature && c.SignedRfce is not null)
            {
                if (Check(d, c.SignedRfce, Rfce(d), business, now) is { IsFailure: true } refused) return refused.Error!;
                (d.RfceXml, d.Status, d.LastError, d.NextAttemptUtc) = (EcfSubmissionQueue.Zip(c.SignedRfce), SubmissionStatus.Pending, null, null);
            }

            if (c.Answer is { } a)
            {
                if (d.Status is SubmissionStatus.AwaitingDeviceSignature) return DeviceSignedErrors.NotSent;
                if (d.Status is not (SubmissionStatus.Pending or SubmissionStatus.Sent)) return DeviceSignedErrors.Final;
                // The store's state machine: Pending → Sent (TrackId, "en proceso") → Accepted / AcceptedConditional / Rejected.
                EcfSubmissionQueue.Apply(d, new DgiiAnswer(a.State, a.TrackId, a.SequenceUsed, a.Messages is null ? null : a.Messages[..Math.Min(a.Messages.Length, 2000)]), now);
            }
            return ItemOf(d, now);

            Result<DeviceSignature.Checked> Check(FiscalDocument doc, string signed, XDocument expected, BusinessProfile b, DateTime at)
            {
                var r = DeviceSignature.Verify(signed, expected);
                if (r.IsSuccess)
                {
                    var certificate = r.Value.Certificate;
                    var taxId = CertificateInfo.Of(certificate, "").TaxId;
                    // As the store's import (R8 #4): the business's RNC, or a person's cédula on a company's certificate (the delegated signer).
                    if (taxId is null || !IEcfCertificateHolder.Fits(taxId, b.Rnc)) r = DeviceSignedErrors.WrongRnc(taxId, b.Rnc);
                    else if (certificate.NotBefore.ToUniversalTime() > at || certificate.NotAfter.ToUniversalTime() < at) r = DeviceSignedErrors.CertificateExpired;
                }
                if (r.IsFailure) log.LogWarning("Refused the device signature of {ENcf}: {Why}", doc.ENcf, r.Error!.Description);
                return r;
            }
        }
    }

    internal sealed class FailureValidator : AbstractValidator<Failure>
    {
        public FailureValidator() => RuleFor(c => c.Message).NotEmpty();
    }

    internal sealed class FailureHandler(FiscalDbContext db, DeviceSigning device, TimeProvider clock) : ICommandHandler<Failure, Item>
    {
        public async Task<Result<Item>> Handle(Failure c, CancellationToken ct)
        {
            var d = await db.Documents.SingleOrDefaultAsync(x => x.Id == c.Id, ct);
            if (!device.Enabled || d is null || d.Environment == FiscalEnvironment.Simulated
                || d.Status is not (SubmissionStatus.AwaitingDeviceSignature or SubmissionStatus.Pending or SubmissionStatus.Sent))
                return DeviceSignedErrors.NotFound;
            var now = clock.GetUtcNow().UtcDateTime;
            d.Attempts++;
            (d.LastError, d.NextAttemptUtc) = (c.Message[..Math.Min(c.Message.Length, 2000)], now + EcfSubmissionQueue.RetryDelay(d.Attempts));
            return ItemOf(d, now);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/fiscal/device/documents").RequireAuthorization().WithTags("Fiscal");
            group.MapGet("", async (FiscalDbContext db, DeviceSigning device, TimeProvider clock, CancellationToken ct) =>
            {
                if (!device.Enabled) return Results.NotFound();
                var now = clock.GetUtcNow().UtcDateTime;
                var documents = await db.Documents.AsNoTracking()
                    .Where(d => (d.Status == SubmissionStatus.AwaitingDeviceSignature
                            || ((d.Status == SubmissionStatus.Pending || d.Status == SubmissionStatus.Sent) && d.Environment != FiscalEnvironment.Simulated))
                        && (d.NextAttemptUtc == null || d.NextAttemptUtc <= now))
                    .OrderBy(d => d.SubmitDeadlineUtc).Take(EcfSubmissionQueue.BatchSize).ToListAsync(ct);
                return Results.Ok(documents.Select(d => ItemOf(d, now)).ToList());
            });
            group.MapPost("/{id:guid}", async (Guid id, Body b, ICommandHandler<Submit, Item> h, CancellationToken ct) =>
                (await h.Handle(new Submit(id, b.SignedXml, b.SignedRfce, b.Answer), ct)).ToHttp());
            group.MapPost("/{id:guid}/failure", async (Guid id, FailureBody b, ICommandHandler<Failure, Item> h, CancellationToken ct) =>
                (await h.Handle(new Failure(id, b.Message), ct)).ToHttp());
        }
    }
}

/// <summary>TK-323: the Lite host has no server-side queue (the devices send): this only copies each business's final documents to the cloud
/// archive, as the queue does after its pass (<see cref="EcfSubmissionQueue.ArchiveAsync"/>), each business as its own node. Off when
/// <c>Sync:Enabled</c> is false (the test host), as the other uploads to the cloud.</summary>
internal sealed class DeviceSignedArchiveWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<DeviceSignedArchiveWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Sync:Enabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue("Fiscal:SubmitIntervalSeconds", 30)), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await scopes.ForEachTenantAsync(sp => sp.GetRequiredService<EcfSubmissionQueue>().ArchiveAsync(stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "The e-CF archive pass failed");
            }
        }
    }
}
