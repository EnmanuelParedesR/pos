using Microsoft.EntityFrameworkCore;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal;

/// <summary>TK-192: the DGII 608 ("Formato de envío de comprobantes anulados"): every e-NCF this store consumed in the period that is not a valid receipt,
/// so the 607 plus the 608 account for every number used.
/// Columns and void types from the DGII: https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscales/Paginas/Formato-608.aspx
/// (Norma General 07-2018 / 05-2019; tool "Formato 608" and its instructivo). Rows:
/// <list type="bullet">
/// <item>Numbers voided with an ANECF (TK-150), one row per number, dated the day of the ANECF, type 08 "Errores en secuencia de NCF".</item>
/// <item>e-CF the DGII rejected (replaced by a new e-CF, TK-147), dated the day they were signed, type 04 "Corrección de la información".</item>
/// </list>
/// A sale voided inside its shift is not here: its e-CF stays valid and the credit note that cancels it (code 1) is in the 607 with it.
/// Documents of the DGII test environments (TesteCF, CerteCF) are left out, as in the 607.</summary>
/// <remarks>shortcut: the void types (08 for an ANECF, 04 for a rejection) are our reading of DGII's table; the accountant confirms them with the
/// open questions of TK-177. An ANECF of a very large range lists every number (the export caps Excel at 50,000 rows; use CSV or TXT).</remarks>
internal sealed class FiscalReports(FiscalDbContext db, EcfCertificate certificate, IEcfProvider provider, TimeProvider clock) : IFiscalReports
{
    public const string SequenceErrors = "08";
    public const string InformationCorrection = "04";

    public async Task<IReadOnlyList<Report608Row>> Report608Async(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (f, t) = BusinessDay.UtcRange(from, to);
        var voids = await db.Voids.Where(v => v.CreatedUtc >= f && v.CreatedUtc < t && v.Status != SubmissionStatus.Rejected
                && v.Environment != FiscalEnvironment.TesteCF && v.Environment != FiscalEnvironment.CerteCF)
            .Select(v => new { v.Type, v.From, v.To, v.CreatedUtc }).AsNoTracking().ToListAsync(ct);
        var rejected = await db.Documents.Where(d => d.SignedUtc >= f && d.SignedUtc < t && d.Status == SubmissionStatus.Rejected
                && d.Environment != FiscalEnvironment.TesteCF && d.Environment != FiscalEnvironment.CerteCF)
            .Select(d => new { d.ENcf, d.SignedUtc }).AsNoTracking().ToListAsync(ct);

        return voids.SelectMany(v => LongRange(v.From, v.To).Select(n => new Report608Row(ENcf.Format(v.Type, n), BusinessDay.Of(v.CreatedUtc), SequenceErrors)))
            .Concat(rejected.Select(d => new Report608Row(d.ENcf, BusinessDay.Of(d.SignedUtc), InformationCorrection)))
            .OrderBy(r => r.DocumentDate).ThenBy(r => r.Ncf, StringComparer.Ordinal).ToList();
    }

    /// <summary>The signals of <see cref="EcfQueueHealthCheck"/> as numbers, for the cloud's alerts (TK-222).</summary>
    public async Task<FiscalHealth> HealthAsync(CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var rejected = await db.Documents.CountAsync(d => d.Status == SubmissionStatus.Rejected && d.ReplacedByENcf == null && d.SignedUtc > since, ct);
        var oldest = await db.Documents.Where(d => d.Status == SubmissionStatus.Pending).MinAsync(d => (DateTime?)d.SignedUtc, ct);
        return new FiscalHealth(rejected, oldest, certificate.Describe()?.NotAfterUtc, provider.Environment.ToString());
    }

    private static IEnumerable<long> LongRange(long from, long to)
    {
        for (var n = from; n <= to; n++) yield return n;
    }
}
