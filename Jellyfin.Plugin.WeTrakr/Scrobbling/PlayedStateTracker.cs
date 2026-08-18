using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Suppresses "ItemMarkedPlayed" dispatches that do not actually change the
/// played state of an item.
///
/// Jellyfin raises UserDataSaved with SaveReason.TogglePlayed every time
/// something writes the played flag, EVEN IF the value is already the same.
/// Other plugins that sync an external history (a Trakt sync task, for
/// instance) re-apply "mark as played" over the whole library on every run, so
/// a single scheduled task turns into hundreds of identical events per pass.
/// WeTrakr saw 35.958 of them in 24h from one server, over 351 items: since
/// each one used to be forwarded, the user's watch time grew without bound.
///
/// This gate remembers the last played value dispatched per (user, item) and
/// lets an event through only when the value actually flips. The first event
/// for an item is always dispatched: a state we have never seen is, as far as
/// this plugin knows, new information.
///
/// Real rewatches are unaffected: they arrive as PlaybackStart / PlaybackStop,
/// which are never gated here. Un-marking and re-marking an item also flips the
/// value twice, so both events are dispatched.
/// </summary>
public class PlayedStateTracker
{
    /// <summary>Entries untouched for longer than this are dropped during a sweep.</summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromHours(12);

    /// <summary>Sweep once the map grows past this many entries.</summary>
    private const int SweepThreshold = 10000;

    private readonly ConcurrentDictionary<string, (bool Played, DateTime LastUtc)> _state = new();

    /// <summary>Key for an item as seen by one user.</summary>
    public static string KeyFor(Guid userId, Guid itemId) => $"{userId:N}|{itemId:N}";

    /// <summary>
    /// Returns true when this played value differs from the last one dispatched
    /// for the same user + item (or when we have never seen the item), recording
    /// the new value. Returns false for a redundant re-mark.
    /// </summary>
    public bool ShouldDispatch(string key, bool played, DateTime nowUtc)
    {
        if (_state.TryGetValue(key, out var previous) && previous.Played == played)
        {
            // Refresh the timestamp so an item that keeps being re-marked does
            // not expire and start passing through again on the next sweep.
            _state[key] = (played, nowUtc);
            return false;
        }

        _state[key] = (played, nowUtc);

        if (_state.Count > SweepThreshold)
        {
            Sweep(nowUtc);
        }

        return true;
    }

    /// <summary>Drops entries older than <see cref="EntryTtl"/>.</summary>
    private void Sweep(DateTime nowUtc)
    {
        var stale = _state
            .Where(kv => nowUtc - kv.Value.LastUtc > EntryTtl)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in stale)
        {
            _state.TryRemove(key, out _);
        }
    }
}
