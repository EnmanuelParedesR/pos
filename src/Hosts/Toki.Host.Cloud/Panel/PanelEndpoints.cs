using System.Security.Claims;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Panel;

internal static class PanelEndpoints
{
    public const int MaxDays = 366;

    public static void MapPanel(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup("/api/panel").RequireAuthorization(CloudAuth.PanelPolicy);

        panel.MapGet("/summary", async (DateOnly? date, Guid? storeId, ClaimsPrincipal user, PanelQueries q, CancellationToken ct) =>
            Results.Ok(await q.SummaryAsync(user.GetTenantId(), storeId, date ?? q.Today, ct)));

        panel.MapGet("/sales/by-day", (DateOnly? from, DateOnly? to, Guid? storeId, ClaimsPrincipal user, PanelQueries q, CancellationToken ct) =>
            Run(user, q, from, to, storeId, f => q.SalesByDayAsync(f, ct)));

        panel.MapGet("/sales/top-products", async (DateOnly? from, DateOnly? to, Guid? storeId, int? limit, ClaimsPrincipal user, PanelQueries q, CloudDbContext db,
            CancellationToken ct) =>
        {
            var showCosts = await PanelAccess.CanViewCostsAsync(user, db, ct);
            return await Run(user, q, from, to, storeId, f => q.TopProductsAsync(f, limit ?? 10, showCosts, ct));
        });

        panel.MapGet("/sales/by-cashier", (DateOnly? from, DateOnly? to, Guid? storeId, ClaimsPrincipal user, PanelQueries q, CancellationToken ct) =>
            Run(user, q, from, to, storeId, f => q.SalesByCashierAsync(f, ct)));

        panel.MapGet("/sales/payment-mix", (DateOnly? from, DateOnly? to, Guid? storeId, ClaimsPrincipal user, PanelQueries q, CancellationToken ct) =>
            Run(user, q, from, to, storeId, f => q.PaymentMixAsync(f, ct)));

        panel.MapGet("/shifts", (DateOnly? from, DateOnly? to, Guid? storeId, bool? onlyDifferences, ClaimsPrincipal user, PanelQueries q, CancellationToken ct) =>
            Run(user, q, from, to, storeId, f => q.ShiftsAsync(f, onlyDifferences ?? false, ct)));

        panel.MapGet("/shifts/{shiftId:guid}", async (Guid shiftId, ClaimsPrincipal user, PanelQueries q, CancellationToken ct) =>
            await q.ShiftReportAsync(user.GetTenantId(), shiftId, ct) is { } report
                ? Results.Ok(report)
                : EndpointExtensions.Problem(Error.NotFound("Panel.ShiftNotFound", "No encontramos ese cierre.")));

        panel.MapGet("/receivables", async (Guid? storeId, int? limit, ClaimsPrincipal user, PanelQueries q, CancellationToken ct) =>
            Results.Ok(await q.ReceivablesAsync(user.GetTenantId(), storeId, limit ?? 10, ct)));

        panel.MapGet("/sync-status", async (ClaimsPrincipal user, PanelQueries q, CancellationToken ct) =>
            Results.Ok(await q.SyncStatusAsync(user.GetTenantId(), ct)));
    }

    /// <summary>Default range: the last 7 store days up to today.</summary>
    private static async Task<IResult> Run<T>(ClaimsPrincipal user, PanelQueries q, DateOnly? from, DateOnly? to, Guid? storeId, Func<PanelFilter, Task<T>> query)
    {
        var end = to ?? q.Today;
        var start = from ?? end.AddDays(-6);
        if (start > end || end.DayNumber - start.DayNumber >= MaxDays)
            return EndpointExtensions.Problem(Error.Validation("Panel.InvalidRange", $"El rango de fechas no es válido (máximo {MaxDays} días)."));
        return Results.Ok(await query(new PanelFilter(user.GetTenantId(), storeId, start, end)));
    }
}
