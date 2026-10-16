using System.Collections.Concurrent;

namespace Toki.Identity;

/// <summary>R11 C12: failed attempts per key (a device, an address, the pairing window) in the last <see cref="Window"/>. Past the limit the key
/// waits until its oldest failure leaves the window. In memory: a restart clears it, which an attacker on the LAN cannot cause.</summary>
internal sealed class AttemptLimiter(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Wrong PINs (any user, sign-in or supervisor approval) from one register: past this, the register waits. Each user still has
    /// their own lockout; this stops one register from trying every user's PIN in turn.</summary>
    public const int PinsPerDevice = 10;
    public static string PinKey(Guid deviceId) => $"pin:{deviceId}";

    /// <summary>R11 A14: wrong PINs for one user from one register (or from one cashier asking for approvals). Under the user's own lockout (5),
    /// so a cashier cannot lock the owner out from their register; several registers still lock a guessed user.</summary>
    public const int PinsPerUser = 4;
    public static string UserPinKey(Guid deviceId, string code, Guid? requester = null) => $"pin:{deviceId}:{requester}:{code}";
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    public bool IsBlocked(string key, int limit)
    {
        if (!_failures.TryGetValue(key, out var list)) return false;
        lock (list) return Recent(list) >= limit;
    }

    /// <returns>The failures of this key in the window, this one included.</returns>
    public int Fail(string key)
    {
        var list = _failures.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.Add(clock.GetUtcNow());
            return Recent(list);
        }
    }

    public void Clear(string key) => _failures.TryRemove(key, out _);

    private int Recent(List<DateTimeOffset> list)
    {
        var since = clock.GetUtcNow() - Window;
        list.RemoveAll(t => t < since);
        return list.Count;
    }
}
