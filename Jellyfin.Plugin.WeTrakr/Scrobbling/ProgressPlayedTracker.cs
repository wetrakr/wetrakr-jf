using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Detects the moment Jellyfin itself marks an item as played DURING playback.
///
/// Jellyfin flips the played flag on a progress tick once the position passes
/// MaxResumePct (90% by default), and saves it with SaveReason.PlaybackProgress.
/// That is Jellyfin's own "watched" decision, and it happens even when the
/// client never reports the item as stopped (autoplay into the next episode).
/// Forwarding it gives WeTrakr the watched mark without depending on the stop.
///
/// Only an OBSERVED flip counts: the item must have been seen unplayed on an
/// earlier tick of this server's lifetime. Every progress tick of a rewatch
/// already carries Played = true (Jellyfin never resets it mid-playback), so
/// treating "played and never seen before" as new information would mark a
/// rewatch watched the second it starts.
/// </summary>
public class ProgressPlayedTracker
{
    /// <summary>Entries untouched for longer than this are dropped during a sweep.</summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromHours(12);

    /// <summary>Sweep once the map grows past this many entries.</summary>
    private const int SweepThreshold = 10000;

    private readonly ConcurrentDictionary<string, (bool Played, DateTime LastUtc)> _state = new();

    /// <summary>
    /// Records the played value seen on a playback save and returns true only
    /// when it went from unplayed to played.
    /// </summary>
    public bool ObserveFlipToPlayed(string key, bool played, DateTime nowUtc)
    {
        var flipped = played
            && _state.TryGetValue(key, out var previous)
            && !previous.Played;

        _state[key] = (played, nowUtc);

        if (_state.Count > SweepThreshold)
        {
            Sweep(nowUtc);
        }

        return flipped;
    }

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
