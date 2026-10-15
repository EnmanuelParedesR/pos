namespace Toki.Sync.Contracts;

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
