using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Holds one in-flight device_code per Jellyfin user during pairing.
/// Keyed by the caller's user id (derived from their token, never the body),
/// so a user can only ever poll their own pending code.
/// </summary>
public class PendingPairingStore
{
    private readonly ConcurrentDictionary<Guid, (string DeviceCode, DateTime Expiry)> _pending = new();

    public void Set(Guid userId, string deviceCode, DateTime expiry)
        => _pending[userId] = (deviceCode, expiry);

    public string? Get(Guid userId)
    {
        if (_pending.TryGetValue(userId, out var entry))
        {
            if (entry.Expiry > DateTime.UtcNow)
            {
                return entry.DeviceCode;
            }

            _pending.TryRemove(userId, out _);
        }

        return null;
    }

    public void Clear(Guid userId) => _pending.TryRemove(userId, out _);
}
