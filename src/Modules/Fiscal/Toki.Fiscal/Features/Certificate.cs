using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Domain;
using Toki.Settings.Contracts;
using Toki.SharedKernel;

namespace Toki.Fiscal.Features;

/// <summary>TK-176: the e-CF certificate as the owner sees it. <c>ExpiresSoon</c> from <see cref="CertificateInfo.WarningDays"/> days before
/// it expires (the Resumen and the register's status bar warn). <c>InUse</c>: the store signs with it (<c>Fiscal:Signer</c> = <c>Certificate</c>);
/// otherwise it is kept ready for when the store moves to the DGII (TK-141).</summary>
internal static class CertificateStatus
{
    public sealed record Response(bool Installed, bool InUse, string? Subject = null, string? TaxId = null, string? Issuer = null, DateTime? NotAfterUtc = null,
        string? Thumbprint = null, string? Store = null, int? DaysLeft = null, bool ExpiresSoon = false)
    {
        public static Response Of(CertificateInfo? c, bool inUse, DateTime nowUtc) => c is null
            ? new(false, inUse)
            : new(true, inUse, c.Subject, c.TaxId, c.Issuer, c.NotAfterUtc, c.Thumbprint, c.Store, Days(c.NotAfterUtc, nowUtc),
                Days(c.NotAfterUtc, nowUtc) <= CertificateInfo.WarningDays);

        private static int Days(DateTime notAfterUtc, DateTime nowUtc) => (int)Math.Floor((notAfterUtc - nowUtc).TotalDays);
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/certificate", (EcfCertificate certificate, IEcfSigner signer, TimeProvider clock) =>
                    Results.Ok(Response.Of(certificate.Describe(), !signer.IsSimulated, clock.GetUtcNow().UtcDateTime)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-176: imports the .p12/.pfx the certification authority delivered. The password is used once to open it and never stored
/// (nor written to the audit trail); where the key is kept, see <see cref="EcfCertificate"/>.</summary>
internal static class ImportCertificate
{
    /// <param name="File">The .p12/.pfx, in Base64.</param>
    public sealed record Command(string File, string? Password) : ICommand<CertificateStatus.Response>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.File).NotEmpty().MaximumLength(200_000).WithMessage("Elija el archivo .p12 o .pfx del certificado.");
    }

    internal sealed class Handler(EcfCertificate certificate, IEcfSigner signer, ISettingsQueries settings, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, CertificateStatus.Response>
    {
        public async Task<Result<CertificateStatus.Response>> Handle(Command c, CancellationToken ct)
        {
            byte[] pfx;
            try { pfx = Convert.FromBase64String(c.File); }
            catch (FormatException) { return FiscalErrors.CertificateUnreadable; }
            var now = clock.GetUtcNow().UtcDateTime;
            var rnc = (await settings.GetBusinessProfileAsync(ct)).Rnc;
            var imported = certificate.Import(pfx, c.Password, now, Rnc.IsValid(rnc) ? rnc : null);
            if (imported.IsFailure) return imported.Error!;
            var i = imported.Value;
            await audit.RecordAsync("fiscal.import-certificate", "EcfCertificate", Guid.Empty, new { i.Subject, i.TaxId, i.Thumbprint, i.NotAfterUtc, i.Store }, ct);
            return CertificateStatus.Response.Of(i, !signer.IsSimulated, now);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/certificate", async (Command c, ICommandHandler<Command, CertificateStatus.Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Ok(r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
