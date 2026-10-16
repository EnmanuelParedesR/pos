using Toki.SharedKernel;

namespace Toki.Sync.Contracts;

/// <summary>The store's main PC says it is alive and how it is used: on start and every hour while connected (TK-190, docs/metricas-producto.md).
/// Counts, flags and totals only: never names, PINs, customers, products or anything that points at a person.</summary>
/// <param name="AppVersion">The store server's version ("1.4.0").</param>
/// <param name="UptimeMinutes">Since the store server (service) started.</param>
/// <param name="ActiveRegisters">Registers with a shift opened in the last 30 store days.</param>
/// <param name="OutboxBacklog">Events not yet confirmed by the cloud.</param>
/// <param name="LastErrorCode">Kind of the last upload failure ("http_503", "unreachable", "other"), never its text; null after a good upload.</param>
/// <param name="Days">Yesterday and today (store days), so yesterday's last hour is not lost.</param>
/// <param name="License">TK-196: added later, so heartbeats of older versions arrive without it (null).</param>
public sealed record NodeHeartbeatV1(
    Guid NodeId, string AppVersion, string Os, int UptimeMinutes, int ActiveRegisters, int OpenShifts, int OutboxBacklog, string? LastErrorCode,
    NodeFeaturesV1 Features, NodeSetupV1 Setup, IReadOnlyList<DailyUsageV1> Days, NodeLicenseV1? License = null) : IntegrationEvent(NodeId);

/// <param name="Status">Trial, Active, Grace or Restricted.</param>
/// <param name="EndsUtc">When the trial ends or the license expires; null for a trial that has not sold yet.</param>
public sealed record NodeLicenseV1(string Status, DateTime? EndsUtc);

/// <param name="OnAccount">Some customer owes on credit (fiado in use).</param>
/// <param name="ScaleConfigured">Null: the register (client) knows it, the store server does not; not reported yet.</param>
/// <param name="PrinterConfigured">Null: same as <paramref name="ScaleConfigured"/>.</param>
/// <param name="FiscalProvider">Who sends the e-CF (<c>Fiscal:Provider</c>): Simulated or Relay.</param>
public sealed record NodeFeaturesV1(bool OnAccount, bool? ScaleConfigured, bool? PrinterConfigured, string FiscalProvider);

/// <summary>Whether the store finished its setup (the cloud gets no business profile event). Flags only: the RNC itself never goes.</summary>
public sealed record NodeSetupV1(bool BusinessNameSet, bool RncSet, int ActiveProducts);

/// <param name="Day">Store business day (UTC−4).</param>
/// <param name="SalesAmount">Completed sales, ITBIS included.</param>
/// <param name="Returns">Money returned that day, ITBIS included.</param>
public sealed record DailyUsageV1(DateOnly Day, int Sales, decimal SalesAmount, int ActiveCashiers, int Voids, decimal Returns);

/// <summary>The store's authenticated channel to the cloud (ADR-0008): every call goes as this node, with <c>Authorization: Node &lt;nodeId&gt;.&lt;nodeSecret&gt;</c>.
/// The secret never leaves the Sync module.</summary>
public interface ICloudClient
{
    /// <summary>POSTs <paramref name="body"/> as JSON (camelCase) to <c>{cloudUrl}{path}</c>.</summary>
    /// <exception cref="InvalidOperationException">The store is not connected to the cloud.</exception>
    Task<HttpResponseMessage> PostAsJsonAsync<T>(string path, T body, CancellationToken ct);
}

/// <summary>A module's reference data the cloud cannot learn from past events (names of users and registers created before their events
/// existed). Called inside the connect command: each source writes its current rows to the outbox as its usual change events.</summary>
public interface ICloudSnapshot
{
    Task EnqueueAsync(CancellationToken ct);
}
