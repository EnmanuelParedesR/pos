using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal.Domain;
using Abastalia.Fiscal.Features;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal;

internal static class EcfOperationsModule
{
    /// <summary>TK-155 to TK-157: certification assistant, operations health and paper contingency.</summary>
    public static IServiceCollection AddEcfOperations(this IServiceCollection services)
    {
        services.AddScoped<EcfOperations>();
        services.AddScoped<DgiiServiceStatus>();
        services.AddHealthChecks().AddCheck<EcfOperationsHealthCheck>("ecf-operations");
        return services;
    }
}

/// <summary>TK-156: what an owner has to look at to keep issuing e-CF, in one list: the queue against the 72 hours, the last time the DGII
/// answered, the certificate, the e-NCF left, and the paper contingency (TK-157) with its 15 and 30 days. The Resumen ("Para revisar"),
/// the "Comprobantes electrónicos" screen and the <c>ecf-operations</c> health check read it.</summary>
internal sealed class EcfOperations(FiscalDbContext db, FiscalDocuments documents, EcfCertificate certificate, TimeProvider clock)
{
    /// <param name="Level"><c>error</c> (red: fiscal deadline or nothing can be issued) or <c>warning</c> (amber).</param>
    /// <summary>How long an e-CF accepted with conditions stays in the list (TK-240).</summary>
    public const int ConditionalDays = 7;

    /// <param name="Code">queue, dgii, certificate, clock, sequences, contingency, regularize, conditional.</param>
    public sealed record Item(string Level, string Code, string Title, string Detail);

    public sealed record Response(FiscalEnvironment Environment, DateTime? OldestPendingUtc, DateTime? LastDgiiContactUtc, DateTime? CertificateExpiresUtc,
        bool CanSign, bool ContingencyActive, IReadOnlyList<Item> Items);

    public async Task<Response> ReviewAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var items = new List<Item>();
        var env = documents.Environment;

        var pending = db.Documents.Where(d => d.Status == SubmissionStatus.Pending);
        var oldest = await pending.MinAsync(d => (DateTime?)d.SignedUtc, ct);
        if (oldest is { } o && now - o > TimeSpan.FromHours(24))
            items.Add(new(now - o > TimeSpan.FromHours(60) ? "error" : "warning", "queue",
                $"Hay comprobantes sin enviar a la DGII desde hace {(int)(now - o).TotalHours} horas",
                "La DGII da 72 horas desde la firma. Revise la conexión a internet de la PC principal; los comprobantes salen solos al volver."));

        DateTime? lastContact = null;
        if (env != FiscalEnvironment.Simulated)
        {
            var sent = await db.Documents.MaxAsync(d => d.SentUtc, ct);
            var result = await db.Documents.MaxAsync(d => d.ResultUtc, ct);
            var voided = await db.Voids.MaxAsync(v => v.ResultUtc, ct);
            lastContact = new[] { sent, result, voided }.Max();
            if (await pending.AnyAsync(d => d.Attempts > 0, ct))
                items.Add(new("warning", "dgii", "No se ha podido hablar con la DGII",
                    (lastContact is { } l ? $"Último contacto: {Local(l)}. " : "")
                    + "Los comprobantes quedan guardados y se envían solos cuando la DGII responda. Use \"Consultar a la DGII\" para ver si el servicio está arriba."));
        }

        // TK-240: accepted with conditions in the last week. Valid, nothing to replace; the owner reads what the DGII observed to fix it next time.
        var conditional = await db.Documents.Where(d => d.Status == SubmissionStatus.AcceptedConditional && d.ResultUtc >= now.AddDays(-ConditionalDays))
            .OrderByDescending(d => d.ResultUtc).Select(d => new { d.ENcf, d.DgiiMessages }).ToListAsync(ct);
        if (conditional is [var first, ..])
            items.Add(new("warning", "conditional", conditional.Count == 1
                    ? $"La DGII aceptó {first.ENcf} con observaciones"
                    : $"La DGII aceptó {conditional.Count} comprobantes con observaciones",
                $"Son válidos y no hay que reemplazarlos. La DGII dice: {first.DgiiMessages ?? "sin mensaje"}. Corríjalo para las próximas facturas."));

        var why = documents.CannotSign();
        var cert = certificate.Describe();
        if (why is not null)
            items.Add(new("error", "certificate", $"No se pueden firmar comprobantes: {why}",
                "Importe un certificado vigente en Configuración → Comprobantes. Si no lo tiene hoy, declare la contingencia en la OFV y actívela aquí para facturar con serie B."));
        else if (cert is not null && (cert.NotAfterUtc - now).TotalDays <= CertificateInfo.WarningDays)
            items.Add(new("warning", "certificate", $"El certificado digital vence el {Local(cert.NotAfterUtc):dd/MM/yyyy}",
                "Renuévelo con la entidad certificadora e impórtelo antes de esa fecha: sin certificado no se puede facturar con e-CF."));

        // TK-156 (D17): the DGII rejects a signature dated after its own time; sales go on and their e-CF wait unsigned.
        if (documents.ClockProblem is { } clockOff)
            items.Add(new("error", "clock", clockOff,
                $"Se sigue vendiendo: {await pending.CountAsync(d => d.SecurityCode == "", ct)} comprobantes esperan sin firmar (el recibo sale sin código de "
                + "seguridad y con la leyenda de contingencia) y se firman y envían solos en cuanto la hora esté bien. En Windows: Configuración → Hora e "
                + "idioma → Fecha y hora → Sincronizar ahora."));

        foreach (var s in (await SequenceAlerts.ComputeAsync(db, env, BusinessDay.Of(now), ct)).Where(s => s.Alert))
            items.Add(new("warning", "sequences", $"Quedan {s.Available} números de {FiscalErrors.Name(s.Type)}",
                "Pida un rango nuevo en la OFV y cárguelo en Configuración → Comprobantes antes de que se acaben."));

        var active = await db.Set<EcfContingency>().AsNoTracking().FirstOrDefaultAsync(c => c.EndedUtc == null, ct);
        if (active is not null)
        {
            var day = PaperContingency.Days(active.StartedUtc, now) + 1;
            items.Add(new("error", "contingency", day > EcfContingency.MaxDays
                    ? $"La contingencia lleva {day} días: pasó el límite de {EcfContingency.MaxDays}"
                    : $"Contingencia activa: día {day} de {EcfContingency.MaxDays}",
                "Se facturan comprobantes serie B en papel. Declárela en la OFV, resuelva el certificado y termine la contingencia para regularizar."));
        }
        else if (await db.Set<PaperDocument>().CountAsync(d => d.RegularizedByENcf == null, ct) is > 0 and var toRegularize)
        {
            var ended = await db.Set<EcfContingency>().MaxAsync(c => c.EndedUtc, ct) ?? now;
            var by = BusinessDay.Of(ended).AddDays(EcfContingency.RegularizeDays);
            items.Add(new(BusinessDay.Of(now) > by ? "error" : "warning", "regularize", $"{toRegularize} comprobantes serie B por regularizar",
                $"Hay que reemplazarlos con e-CF a más tardar el {by:dd/MM/yyyy} (30 días desde que terminó la contingencia)."));
        }

        return new Response(env, oldest, lastContact, cert?.NotAfterUtc, why is null, active is not null,
            [.. items.OrderBy(i => i.Level == "error" ? 0 : 1)]);
    }

    private static DateTime Local(DateTime utc) => utc + BusinessDay.Offset;

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/health", async (EcfOperations operations, CancellationToken ct) => Results.Ok(await operations.ReviewAsync(ct)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Degraded while anything in <see cref="EcfOperations"/> needs the owner. Never Unhealthy: <c>/health</c> is also how the register
/// knows the store server answers, and a fiscal warning must not look like the server being down.</summary>
internal sealed class EcfOperationsHealthCheck(EcfOperations operations) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // A conditional acceptance (TK-240) is a valid e-CF: worth reading, not an operations problem.
        var items = (await operations.ReviewAsync(cancellationToken)).Items.Where(i => i.Code != "conditional").ToList();
        return items.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(string.Join(" ", items.Select(i => $"[{i.Code}] {i.Title}.")));
    }
}

/// <summary>TK-156: whether the DGII web services answer right now, asked on demand (and by the worker to measure the clock, <see cref="DgiiClock"/>).
/// The seed of the authentication service (public, no token) stands for "the DGII is up". The DGII's own "FE Estatus Servicios API"
/// (<see cref="StatusHost"/>, docs/ecf-propio.md §11 #6) needs an API key the DGII has not given, so it is not read; when the seed does not
/// answer at all, any answer from that host (even its 401) only tells "the DGII is reachable, its e-CF service is not" from "this store is offline".</summary>
internal sealed class DgiiServiceStatus(IEcfProvider provider, IServiceProvider services, TimeProvider clock)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public const string StatusHost = "https://statusecf.dgii.gov.do";

    /// <param name="State"><c>Simulated</c> (nothing goes to the DGII), <c>Up</c>, <c>Down</c> (the DGII answers with an error or only its other
    /// hosts answer: a DGII problem) or <c>Unreachable</c> (no DGII host answers: most likely this store's internet).</param>
    public sealed record Response(string State, string Message, DateTime CheckedUtc);

    public async Task<Response> CheckAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (provider.IsSimulated) return new("Simulated", "La tienda está en modo simulado: no se envía nada a la DGII.", now);
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(DgiiClient.HttpClientName);
        var watch = Stopwatch.StartNew();
        var seed = await AnswerAsync(http, $"{new DgiiUrls(provider.Environment).Ecf}/autenticacion/api/autenticacion/semilla", ct);
        if (seed is >= 200 and < 300) return new("Up", $"La DGII respondió en {watch.ElapsedMilliseconds} ms.", now);
        if (seed is not null)
            return new("Down", $"La DGII respondió con un error ({seed}). Puede ser una ventana de mantenimiento: los comprobantes esperan en la cola.", now);
        return await AnswerAsync(http, $"{StatusHost}/", ct) is not null
            ? new("Down", "La DGII no responde en el servicio de comprobantes, pero su sitio sí: es una intermitencia de la DGII, no de esta tienda. "
                + "Los comprobantes esperan en la cola (72 horas).", now)
            : new("Unreachable", "No se llega a ningún servicio de la DGII. Revise el internet de la PC principal; los comprobantes esperan en la cola (72 horas).", now);
    }

    /// <returns>The HTTP status, or null when nothing answered within <see cref="Timeout"/>.</returns>
    private static async Task<int?> AnswerAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return (int)response.StatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/dgii-status", async (DgiiServiceStatus status, CancellationToken ct) => Results.Ok(await status.CheckAsync(ct)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
