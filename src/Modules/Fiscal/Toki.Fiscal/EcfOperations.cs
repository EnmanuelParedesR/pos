using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Domain;
using Toki.Fiscal.Features;
using Toki.SharedKernel;

namespace Toki.Fiscal;

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
    /// <param name="Code">queue, dgii, certificate, sequences, contingency, regularize.</param>
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

        var why = documents.CannotSign();
        var cert = certificate.Describe();
        if (why is not null)
            items.Add(new("error", "certificate", $"No se pueden firmar comprobantes: {why}",
                "Importe un certificado vigente en Configuración → Comprobantes. Si no lo tiene hoy, declare la contingencia en la OFV y actívela aquí para facturar con serie B."));
        else if (cert is not null && (cert.NotAfterUtc - now).TotalDays <= CertificateInfo.WarningDays)
            items.Add(new("warning", "certificate", $"El certificado digital vence el {Local(cert.NotAfterUtc):dd/MM/yyyy}",
                "Renuévelo con la entidad certificadora e impórtelo antes de esa fecha: sin certificado no se puede facturar con e-CF."));

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
        var review = await operations.ReviewAsync(cancellationToken);
        return review.Items.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(string.Join(" ", review.Items.Select(i => $"[{i.Code}] {i.Title}.")));
    }
}

/// <summary>TK-156: whether the DGII web services answer right now, asked on demand. The DGII "Estatus de servicios" endpoint is not known
/// yet (docs/ecf-propio.md §11 #6): meanwhile the seed of the authentication service (public, no token) stands for "the DGII is up".</summary>
internal sealed class DgiiServiceStatus(IEcfProvider provider, IServiceProvider services, TimeProvider clock)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <param name="State"><c>Simulated</c> (nothing goes to the DGII), <c>Up</c> or <c>Down</c>.</param>
    public sealed record Response(string State, string Message, DateTime CheckedUtc);

    public async Task<Response> CheckAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (provider.IsSimulated) return new("Simulated", "La tienda está en modo simulado: no se envía nada a la DGII.", now);
        var url = $"{new DgiiUrls(provider.Environment).Ecf}/autenticacion/api/autenticacion/semilla";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await services.GetRequiredService<IHttpClientFactory>().CreateClient(DgiiClient.HttpClientName).GetAsync(url, timeout.Token);
            return response.IsSuccessStatusCode
                ? new("Up", $"La DGII respondió en {watch.ElapsedMilliseconds} ms.", now)
                : new("Down", $"La DGII respondió con un error ({(int)response.StatusCode}). Puede ser una ventana de mantenimiento: los comprobantes esperan en la cola.", now);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Down", "No se pudo conectar con la DGII. Revise el internet de la PC principal; los comprobantes esperan en la cola (72 horas).", now);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/dgii-status", async (DgiiServiceStatus status, CancellationToken ct) => Results.Ok(await status.CheckAsync(ct)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
