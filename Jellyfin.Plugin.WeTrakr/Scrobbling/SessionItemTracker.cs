using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Remembers, per Jellyfin session, which item is playing and the last
/// position the client reported for it, so a missing PlaybackStopped can be
/// replaced by a synthetic one.
///
/// Many clients jump straight to the next episode (autoplay, "next" button)
/// without reporting the previous item as stopped. Jellyfin does not fill the
/// gap either: its idle check only stops the session's CURRENT item, which by
/// then is already the new one. Without a stop the previous episode never got
/// its watched mark on WeTrakr and stayed "playing" at 90-100%. WeTrakr saw a
/// third of one user's episode switches arrive like that.
///
/// The plugin sees every progress tick (they are throttled before leaving the
/// server, not before reaching here), so the position kept here is close to
/// where the user actually left the previous item.
/// </summary>
public class SessionItemTracker
{
    /// <summary>Entries untouched for longer than this are dropped during a sweep.</summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromHours(12);

    /// <summary>Sweep once the map grows past this many entries.</summary>
    private const int SweepThreshold = 1000;

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _bySession = new();

    /// <summary>What one session was last seen playing.</summary>
    public sealed class Entry
    {
        public Entry(BaseItem item, SessionInfo session, string playKey, long positionTicks, DateTime lastUtc)
        {
            Item = item;
            Session = session;
            PlayKey = playKey;
            PositionTicks = positionTicks;
            LastUtc = lastUtc;
        }

        public BaseItem Item { get; }

        public SessionInfo Session { get; }

        /// <summary>Key the pause tracker and the progress throttle used for this playback.</summary>
        public string PlayKey { get; }

        public long PositionTicks { get; set; }

        public DateTime LastUtc { get; set; }
    }

    /// <summary>
    /// Records that <paramref name="item"/> started on the session. Returns the
    /// entry of the PREVIOUS item when the session was still playing a different
    /// one without having reported it stopped; null otherwise.
    /// </summary>
    public Entry? Begin(string sessionId, BaseItem item, SessionInfo session, string playKey, long positionTicks, DateTime nowUtc)
    {
        lock (_lock)
        {
            _bySession.TryGetValue(sessionId, out var previous);
            _bySession[sessionId] = new Entry(item, session, playKey, positionTicks, nowUtc);

            if (_bySession.Count > SweepThreshold)
            {
                Sweep(nowUtc);
            }

            return previous != null && previous.Item.Id != item.Id ? previous : null;
        }
    }

    /// <summary>Updates the last known position, if the session is still on this item.</summary>
    public void Update(string sessionId, BaseItem item, long? positionTicks, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_bySession.TryGetValue(sessionId, out var entry) || entry.Item.Id != item.Id)
            {
                return;
            }

            if (positionTicks.HasValue)
            {
                entry.PositionTicks = positionTicks.Value;
            }

            entry.LastUtc = nowUtc;
        }
    }

    /// <summary>
    /// Forgets the session's item after a real stop. A stop for an item the
    /// session already left (it arrived after the next PlaybackStart) leaves the
    /// new entry alone.
    /// </summary>
    public void End(string sessionId, Guid itemId)
    {
        lock (_lock)
        {
            if (_bySession.TryGetValue(sessionId, out var entry) && entry.Item.Id == itemId)
            {
                _bySession.Remove(sessionId);
            }
        }
    }

    private void Sweep(DateTime nowUtc)
    {
        var stale = new List<string>();
        foreach (var kv in _bySession)
        {
            if (nowUtc - kv.Value.LastUtc > EntryTtl)
            {
                stale.Add(kv.Key);
            }
        }

        foreach (var key in stale)
        {
            _bySession.Remove(key);
        }
    }
}
