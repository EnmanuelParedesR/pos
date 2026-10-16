using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Abastalia.Client.UI.Api;

namespace Abastalia.Client.UI.State;

/// <summary>Where each host keeps this PC's pairing between runs (WPF: encrypted with DPAPI for the Windows user; the dev web host: a local file).</summary>
public interface ILocalStore
{
    Task<string?> ReadAsync(string key);
    Task SaveAsync(string key, string? value);
}

/// <summary>Who is at this PC: the paired device, the logged-in user and their open shift.</summary>
public sealed class Session(ILocalStore store, NodeCertificatePin pin, ServerAddress? server = null, IDeviceKeys? deviceKeys = null)
{
    private readonly IDeviceKeys _keys = deviceKeys ?? new ExportableDeviceKeys();
    /// <summary>R11 C5: the client certificate is renewed when less than this is left (it lasts a year).</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private string? _pendingReference;
    private const string ServerKey = "server";
    private const string DeviceKey = "device";
    private const string NodeCertificateKey = "node-certificate";
    private const string StoreCaKey = "store-ca";
    private const string ClientCertificateKey = "device-certificate";
    private const string ClientKeyKey = "device-key";
    private HashSet<string> _permissions = [];
    private bool _loaded;
    private ECDsa? _pendingKey;

    /// <summary>The device secret of a PC paired over plain http (development) or before TK-091; null with a client certificate.</summary>
    public string? DeviceToken { get; private set; }
    public string? Token { get; private set; }
    public Guid UserId { get; private set; }
    public string? Name { get; private set; }
    public CurrentShift? Shift { get; set; }
    /// <summary>The session ended on the server (12 h, PIN change, revoked device).</summary>
    public bool Expired { get; private set; }
    /// <summary>Whether the last call reached the store server (the status bar shows it; Cobrar waits for it).</summary>
    public bool Online { get; private set; } = true;
    /// <summary>R8 #9: the last call was refused before sending anything because the server is not the pinned one. Not "no network":
    /// the register offers to pair again.</summary>
    public bool UnknownServer { get; private set; }

    public event Action? Changed;

    public bool IsPaired => DeviceToken is not null || pin.ClientCertificate is not null;
    public bool IsOpen => Token is not null;
    public bool Can(string permission) => _permissions.Contains(permission);
    /// <summary>Owners, supervisors, the accountant and whoever does not sell (Inventario) land on the back office; cashiers go straight to the register.</summary>
    public bool IsBackOffice => Can("reports.view") || Can("shifts.view") || Can("fiscal.reports") || !Can("sales.create");

    public async Task LoadAsync()
    {
        if (_loaded) return;
        if (server is not null && await store.ReadAsync(ServerKey) is { } saved && Uri.TryCreate(saved, UriKind.Absolute, out var address)) server.Current = address;
        // The pin before the credentials: no request carries this PC's certificate or secret to a server it has not pinned.
        var ca = await store.ReadAsync(StoreCaKey) is { } caPem ? X509Certificate2.CreateFromPem(caPem) : null;
        pin.Pin(ca, await store.ReadAsync(NodeCertificateKey));
        if (await store.ReadAsync(ClientCertificateKey) is { } certificate && await store.ReadAsync(ClientKeyKey) is { } key)
        {
            try { pin.UseClientCertificate(_keys.Load(certificate, key)); }
            catch (CryptographicException) { } // the key is gone (another Windows user, a reinstalled PC): this PC pairs again
        }
        DeviceToken = await store.ReadAsync(DeviceKey);
        _loaded = true;
    }

    /// <summary>TK-251: a register joining a store points at the main PC (name or IP). Kept for every later run; false when it is not a host.</summary>
    public async Task<bool> UseServerAsync(string? host)
    {
        if (server?.With(host) is not { } address) return false;
        await store.SaveAsync(ServerKey, address.ToString());
        server.Current = address;
        return true;
    }

    /// <summary>TK-091: a new key pair for this PC and its CSR, sent with the pairing code. Null over plain http (development): no TLS, no certificate.</summary>
    public string? CertificateRequest()
    {
        if (pin.Seen is null) return null;
        DropPendingKey();
        (_pendingKey, _pendingReference) = _keys.Create();
        return new CertificateRequest("CN=Abastalia Caja", _pendingKey, HashAlgorithmName.SHA256).CreateSigningRequestPem();
    }

    private void DropPendingKey()
    {
        _pendingKey?.Dispose();
        if (_pendingReference is not null) _keys.Delete(_pendingReference);
        (_pendingKey, _pendingReference) = (null, null);
    }

    /// <summary>R11 C5: in its last <see cref="RenewBefore"/> the client certificate is renewed over mTLS with a new key; the old key is
    /// deleted once the new certificate is saved. Nothing changes when the server does not answer: it is tried again next start.</summary>
    /// <param name="renew">Sends the CSR with the current certificate and returns the new certificate (PEM), or null.</param>
    public async Task RenewCertificateIfDueAsync(Func<string, Task<string?>> renew, DateTime nowUtc)
    {
        if (pin.ClientCertificate is not { } current || current.NotAfter.ToUniversalTime() - nowUtc > RenewBefore) return;
        var (key, reference) = _keys.Create();
        using (key)
        {
            if (await renew(new CertificateRequest("CN=Abastalia Caja", key, HashAlgorithmName.SHA256).CreateSigningRequestPem()) is not { } certificate)
            {
                _keys.Delete(reference);
                return;
            }
            var old = await store.ReadAsync(ClientKeyKey);
            var client = _keys.Load(certificate, reference);
            await store.SaveAsync(ClientCertificateKey, certificate);
            await store.SaveAsync(ClientKeyKey, reference);
            pin.UseClientCertificate(client);
            if (old is not null) _keys.Delete(old);
        }
    }

    /// <summary>Saves what the server answered when pairing, if the server is still the one whose fingerprint was shown (R8 #11).</summary>
    /// <param name="shown">SHA-256 of the server certificate whose fingerprint the person compared; null over plain http.</param>
    /// <returns>False when the server changed since it was shown, or its certificate was not issued by the CA it sent: nothing is saved.</returns>
    public async Task<bool> PairAsync(string? shown, string? token, string? certificate = null, string? storeCa = null)
    {
        if (!string.Equals(pin.Seen, shown, StringComparison.OrdinalIgnoreCase)) return false;
        if (certificate is not null && storeCa is not null && _pendingReference is not null && shown is not null)
        {
            var ca = X509Certificate2.CreateFromPem(storeCa);
            // Normally the server's certificate comes from the CA: pin the CA, and the server can renew its own. A server still in dual mode
            // (ADR-0009) shows its old self-signed one: pin that too, until it moves to the CA's.
            var leaf = pin.SawIssuedBy(shown, ca) ? null : shown;
            var client = _keys.Load(certificate, _pendingReference);
            await store.SaveAsync(StoreCaKey, storeCa);
            await store.SaveAsync(NodeCertificateKey, leaf);
            await store.SaveAsync(ClientCertificateKey, certificate);
            await store.SaveAsync(ClientKeyKey, _pendingReference);
            pin.Pin(ca, leaf);
            pin.UseClientCertificate(client);
            _pendingReference = null; // kept: it is this PC's key now
        }
        else if (token is not null)
        {
            await store.SaveAsync(NodeCertificateKey, shown);
            pin.Pin(shown);
            DeviceToken = token;
            await store.SaveAsync(DeviceKey, token);
        }
        else return false;
        DropPendingKey();
        Changed?.Invoke();
        return true;
    }

    /// <summary>The owner revoked this PC, or its server changed: it pairs again from scratch, pin and certificate included.</summary>
    public async Task ForgetDeviceAsync()
    {
        DeviceToken = null;
        UnknownServer = false;
        Close();
        if (await store.ReadAsync(ClientKeyKey) is { } reference) _keys.Delete(reference);
        foreach (var key in new[] { DeviceKey, NodeCertificateKey, StoreCaKey, ClientCertificateKey, ClientKeyKey }) await store.SaveAsync(key, null);
        pin.Pin(null, null);
        pin.UseClientCertificate(null);
    }

    private const string SampleDataKey = "sample-data";

    /// <summary>TK-196: the setup wizard's "datos de ejemplo" box. Loading them needs a session, so the owner's first sign-in does it.</summary>
    public Task AskForSampleDataAsync() => store.SaveAsync(SampleDataKey, "pending");

    /// <summary>True once, when the wizard asked for sample data.</summary>
    public async Task<bool> TakeSampleDataRequestAsync()
    {
        if (await store.ReadAsync(SampleDataKey) is null) return false;
        await store.SaveAsync(SampleDataKey, null);
        return true;
    }

    private const string BusinessKey = "business";

    /// <summary>TK-238: the business data typed in "Primera instalación". Saving it needs settings.manage, so the owner's first sign-in does it.</summary>
    public Task AskForBusinessAsync(BusinessProfile business) => store.SaveAsync(BusinessKey, System.Text.Json.JsonSerializer.Serialize(business));

    /// <summary>The business data of the wizard, once.</summary>
    public async Task<BusinessProfile?> TakeBusinessRequestAsync()
    {
        if (await store.ReadAsync(BusinessKey) is not { } json) return null;
        await store.SaveAsync(BusinessKey, null);
        return System.Text.Json.JsonSerializer.Deserialize<BusinessProfile>(json);
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

    internal void SetOnline(bool online, bool unknownServer = false)
    {
        if (Online == online && UnknownServer == unknownServer) return;
        (Online, UnknownServer) = (online, unknownServer);
        Changed?.Invoke();
    }

    internal void Expire()
    {
        Close();
        Expired = true;
        Changed?.Invoke();
    }
}
