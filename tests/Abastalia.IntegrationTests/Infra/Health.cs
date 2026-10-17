using System.Net.Http.Json;
using System.Text.Json;

namespace Abastalia.IntegrationTests.Infra;

public static class Health
{
    /// <summary>R12: the worst status of Configuración → Salud (/api/settings/health); /health only says the server is up.</summary>
    public static async Task<string> HealthAsync(this AbastaliaApp app)
    {
        var admin = app.Client(await app.AdminAsync()); // not a new sign-in: the clock checks may refuse to save one
        var statuses = (await admin.GetFromJsonAsync<List<JsonElement>>("/api/settings/health"))!.Select(c => c.GetProperty("status").GetString()).ToList();
        return statuses.Contains("Unhealthy") ? "Unhealthy" : statuses.Contains("Degraded") ? "Degraded" : "Healthy";
    }
}
