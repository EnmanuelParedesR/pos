using Toki.Client.UI.Api;

namespace Toki.Client.UI.State;

/// <summary>Where each host keeps the device token between runs (WPF: encrypted per Windows user; the dev web host: a local file).</summary>
public interface ILocalStore
{
    Task<string?> ReadAsync(string key);
    Task SaveAsync(string key, string? value);
}

/// <summary>Who is at this PC: the paired device, the logged-in user and their open shift.</summary>
public sealed class Session(ILocalStore store, NodeCertificatePin pin)
{
    private const string DeviceKey = "device";
    private const string NodeCertificateKey = "node-certificate";
    private HashSet<string> _permissions = [];
    private bool _loaded;

    public string? DeviceToken { get; private set; }
    public string? Token { get; private set; }
    public Guid UserId { get; private set; }
    public string? Name { get; private set; }
    public CurrentShift? Shift { get; set; }
    /// <summary>The session ended on the server (12 h, PIN change, revoked device).</summary>
    public bool Expired { get; private set; }
    /// <summary>Whether the last call reached the store server (the status bar shows it; Cobrar waits for it).</summary>
    public bool Online { get; private set; } = true;

    public event Action? Changed;

    public bool IsPaired => DeviceToken is not null;
    public bool IsOpen => Token is not null;
    public bool Can(string permission) => _permissions.Contains(permission);
    /// <summary>Owners, supervisors, the accountant and whoever does not sell (Inventario) land on the back office; cashiers go straight to the register.</summary>
    public bool IsBackOffice => Can("reports.view") || Can("shifts.view") || Can("fiscal.reports") || !Can("sales.create");

    public async Task LoadAsync()
    {
        if (_loaded) return;
        // The pin before the token: no request carries this PC's secret to a server it has not pinned.
        if (await store.ReadAsync(NodeCertificateKey) is { } thumbprint) pin.Pin(thumbprint);
        DeviceToken = await store.ReadAsync(DeviceKey);
        _loaded = true;
    }

    /// <summary>Saves the device token and pins the server certificate the pairing went through (none over plain http).</summary>
    public async Task PairAsync(string token)
    {
        var seen = pin.Pinned ?? pin.Seen;
        await store.SaveAsync(NodeCertificateKey, seen);
        pin.Pin(seen);
        DeviceToken = token;
        await store.SaveAsync(DeviceKey, token);
        Changed?.Invoke();
    }

    /// <summary>The owner revoked this PC: it pairs again from scratch, pin included.</summary>
    public async Task ForgetDeviceAsync()
    {
        DeviceToken = null;
        Close();
        await store.SaveAsync(DeviceKey, null);
        await store.SaveAsync(NodeCertificateKey, null);
        pin.Pin(null);
    }

    public void Open(OpenedSession s, Me me)
    {
        (Token, UserId, Name, _permissions, Expired) = (s.Token, s.UserId, s.Name, [.. me.Permissions], false);
        Changed?.Invoke();
    }

    public void Close()
    {
        (Token, UserId, Name, _permissions, Shift) = (null, Guid.Empty, null, [], null);
        Changed?.Invoke();
    }

    internal void SetOnline(bool online)
    {
        if (Online == online) return;
        Online = online;
        Changed?.Invoke();
    }

    internal void Expire()
    {
        Close();
        Expired = true;
        Changed?.Invoke();
    }
}
