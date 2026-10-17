using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Security;

namespace Abastalia.Settings;

/// <summary>TK-238: every <c>/health</c> check for the owner (Configuración → Salud), in Spanish, with what to do when it is not well.
/// <c>/health</c> itself stays anonymous and says only Healthy, Degraded or Unhealthy.</summary>
internal static class StoreHealth
{
    /// <param name="Status">Healthy, Degraded or Unhealthy.</param>
    public sealed record Check(string Name, string Status, string Title, string? Detail, string? Action);

    private static readonly Dictionary<string, (string Title, string Problem, string Action)> Guide = new()
    {
        ["backup"] = ("Respaldos", "Los respaldos no están al día.", "Revise la frase y la carpeta de copia en Configuración → Respaldos, y haga uno con \"Respaldar ahora\"."),
        ["store-ca"] = ("Cajas unidas", "Hay cajas que deben volver a unirse.", "En Configuración → Equipos revoque esas cajas y únalas otra vez con un código nuevo."),
        ["pin-failures"] = ("PIN incorrectos", "Alguien escribió varios PIN incorrectos.", "Revise en Auditoría quién lo intentó y desde qué caja; si no fue la persona, cámbiele el PIN."),
        ["shared-pin"] = ("PIN compartido", "El mismo usuario trabajó en dos cajas a la vez.",
            "Dé a cada persona su propio usuario y PIN, y cambie el PIN de ese usuario en Configuración → Usuarios."),
        ["outbox"] = ("Tareas internas", "Hay tareas internas esperando más de 10 minutos.", "Reinicie la PC principal. Si sigue igual, llame al soporte de Abastalia."),
        ["clock"] = ("Hora de la PC", "La hora de la PC principal está atrasada: las facturas y los turnos saldrían con fechas que retroceden.",
            "Corrija la fecha y la hora de Windows en la PC principal (sincronizar ahora)."),
        ["ecf-queue"] = ("Cola de comprobantes (DGII)", "Hay comprobantes atrasados, rechazados o sin respuesta de la DGII.",
            "Abra Comprobantes electrónicos: reemplace los rechazados y revise el internet de la PC principal (hay 72 horas para enviar)."),
        ["ecf-operations"] = ("Comprobantes electrónicos", "Algo impide o pone en riesgo la facturación electrónica.", "Abra Comprobantes electrónicos y siga lo que indica."),
        ["audit"] = ("Auditoría", "La cadena de auditoría está rota: alguien cambió datos por fuera de Abastalia.", "Llame al soporte de Abastalia y no borre nada."),
        ["sync"] = ("Nube", "Hay datos sin subir a la nube.", "Revise el internet de la PC principal; los datos suben solos al volver."),
        ["disk-encryption"] = ("Cifrado del disco", "El disco de la PC principal no está cifrado: si la roban, se leen las ventas, los respaldos y la llave de los comprobantes.",
            "Active BitLocker o el Cifrado de dispositivo de Windows en la PC principal y guarde la clave de recuperación fuera de la PC (guía \"PC robada\")."),
    };

    /// <summary>The checks whose own descriptions are not written for the owner (English, for the logs).</summary>
    private static readonly HashSet<string> TechnicalDescriptions = ["outbox", "clock", "ecf-queue", "disk-encryption"];

    public static Check Describe(string name, HealthReportEntry e)
    {
        var (title, problem, action) = Guide.TryGetValue(name, out var g) ? g : (name, "Revise este punto.", "Llame al soporte de Abastalia.");
        if (e.Status == HealthStatus.Healthy) return new Check(name, e.Status.ToString(), title, null, null);
        var detail = TechnicalDescriptions.Contains(name) || string.IsNullOrWhiteSpace(e.Description) ? problem : e.Description;
        return new Check(name, e.Status.ToString(), title, detail, action);
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/settings/health", async (HealthCheckService health, CancellationToken ct) =>
                    Results.Ok((await health.CheckHealthAsync(ct)).Entries.Select(e => Describe(e.Key, e.Value))
                        .OrderBy(c => c.Status == "Healthy").ThenBy(c => c.Title).ToList()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
    }
}

/// <summary>R11 C2: Degraded when the main PC's system drive is not protected by BitLocker or Device Encryption. Windows only tells an
/// administrator, so the hourly SYSTEM task (deploy/installer/update-service.ps1) writes <c>disk-encryption.json</c> and this reads it.
/// No file (development, another OS, the task has not run yet): nothing to say.</summary>
internal sealed class DiskEncryptionHealthCheck : IHealthCheck
{
    public static string StatusFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Abastalia", "disk-encryption.json");

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string? json = null;
        try { if (OperatingSystem.IsWindows() && File.Exists(StatusFile)) json = File.ReadAllText(StatusFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Task.FromResult(Evaluate(json));
    }

    /// <summary>ProtectionStatus of Win32_EncryptableVolume: 1 = protected; 0, missing (no BitLocker on this Windows) or anything else = not.</summary>
    internal static HealthCheckResult Evaluate(string? json)
    {
        if (json is null) return HealthCheckResult.Healthy();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ProtectionStatus", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number && s.GetInt32() == 1
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("The system drive is not encrypted (BitLocker or Device Encryption).");
        }
        catch (System.Text.Json.JsonException)
        {
            return HealthCheckResult.Healthy(); // half-written by the task: it is written again within the hour
        }
    }
}
