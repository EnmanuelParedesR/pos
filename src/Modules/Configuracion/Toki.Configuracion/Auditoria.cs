using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.Configuracion;

/// <summary>Checks the audit hash chain on demand: anyone who edits or deletes audit rows in the database breaks it.</summary>
internal static class VerificarAuditoria
{
    public sealed record Response(bool Intacta, long? PrimerRegistroRoto);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/auditoria/verificar", async (InfraDbContext db, CancellationToken ct) =>
                    await CadenaAuditoria.VerificarAsync(db, ct) is { } roto ? Results.Ok(new Response(false, roto)) : Results.Ok(new Response(true, null)))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Auditoría");
    }
}

/// <summary>Result of the last nightly check; the "auditoria" health check reports it.</summary>
internal sealed class UltimaVerificacionAuditoria : IHealthCheck
{
    public long? PrimerRegistroRoto { get; set; }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(PrimerRegistroRoto is { } roto
            ? HealthCheckResult.Unhealthy($"La cadena de auditoría está rota desde el registro {roto}.")
            : HealthCheckResult.Healthy());
}

/// <summary>Verifies the audit chain every night at 03:00 store time; a broken chain is a critical log line and an unhealthy /health.</summary>
internal sealed class VerificacionNocturnaAuditoria(IServiceScopeFactory scopes, UltimaVerificacionAuditoria ultima, TimeProvider reloj,
    ILogger<VerificacionNocturnaAuditoria> log) : BackgroundService
{
    private static readonly TimeSpan Hora = TimeSpan.FromHours(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(HastaLaProxima(reloj.GetUtcNow().UtcDateTime), reloj, stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                ultima.PrimerRegistroRoto = await CadenaAuditoria.VerificarAsync(scope.ServiceProvider.GetRequiredService<InfraDbContext>(), stoppingToken);
                if (ultima.PrimerRegistroRoto is { } roto) log.LogCritical("La cadena de auditoría está rota desde el registro {Secuencia}", roto);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "No se pudo verificar la auditoría");
            }
        }
    }

    public static TimeSpan HastaLaProxima(DateTime ahoraUtc)
    {
        var local = ahoraUtc + DiaNegocio.Desfase;
        var proxima = local.Date + Hora;
        if (proxima <= local) proxima = proxima.AddDays(1);
        return proxima - local;
    }
}
