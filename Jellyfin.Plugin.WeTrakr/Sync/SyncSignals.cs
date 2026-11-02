using System.Collections.Concurrent;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>
/// "Something of this account changed in Jellyfin" notices for the sync
/// scheduler, with the items involved. Left by the scrobbler (any user-data
/// save that is not the sync's own, a playback stop, an event that failed to
/// reach WeTrakr) and picked up by SyncScheduler on its next tick, which then
/// uploads the diff of just those items (Local mode).
///
/// Jellyfin cannot list "items whose user data changed since X"
/// (MinDateLastSavedForUser filters on the item's metadata date), so the
/// events are the only cheap source. The daily full pass covers anything lost
/// here, a server restart for instance.
/// </summary>
public static class SyncSignals
{
    private const int MaxItemsPerUser = 5000;

    private sealed class Pending
    {
        public DateTime Since;
        public readonly HashSet<Guid> Items = new();
    }

    private static readonly ConcurrentDictionary<string, Pending> Dirty = new();

    /// <summary>Flags the account (GUID "N") as having local changes, optionally for one item.</summary>
    public static void MarkDirty(string userId, Guid? itemId = null)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var p = Dirty.GetOrAdd(userId, _ => new Pending());
        lock (p)
        {
            p.Since = DateTime.UtcNow;
            if (itemId != null && itemId != Guid.Empty && p.Items.Count < MaxItemsPerUser) p.Items.Add(itemId.Value);
        }
    }

    /// <summary>Last notice of the account, or null when nothing is pending.</summary>
    public static DateTime? DirtySince(string userId)
    {
        if (!Dirty.TryGetValue(userId, out var p)) return null;
        lock (p) return p.Since;
    }

    /// <summary>Takes the pending items and clears the notice if none arrived after `seen`.</summary>
    public static List<Guid> Take(string userId, DateTime seen)
    {
        if (!Dirty.TryGetValue(userId, out var p)) return new List<Guid>();
        lock (p)
        {
            var items = p.Items.ToList();
            p.Items.Clear();
            if (p.Since <= seen) Dirty.TryRemove(userId, out _);
            return items;
        }
    }
}
