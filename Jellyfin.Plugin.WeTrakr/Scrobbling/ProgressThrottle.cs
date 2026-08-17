using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Rate-limits plain "PlaybackProgress" dispatches per playback session.
/// Jellyfin raises PlaybackProgress continuously (every few seconds) for every
/// active session; forwarding each one floods the WeTrakr API. This gate lets
/// at most one progress event through per session per <see cref="MinInterval"/>.
///
/// Start / Stop / Pause / Unpause are NOT throttled — they are meaningful,
/// infrequent transitions and must always be dispatched.
/// </summary>
public class ProgressThrottle
{
    /// <summary>Minimum time between two forwarded PlaybackProgress events for the same session.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, DateTime> _lastSentUtc = new();

    /// <summary>
    /// Returns true if a progress event for this session may be dispatched now,
    /// recording the timestamp when it does. Returns false while inside the
    /// cooldown window. Progress ticks for a single session arrive sequentially,
    /// so the check-then-set does not need extra locking.
    /// </summary>
    public bool ShouldDispatch(string sessionKey, DateTime nowUtc)
    {
        if (_lastSentUtc.TryGetValue(sessionKey, out var last) && nowUtc - last < MinInterval)
        {
            return false;
        }

        _lastSentUtc[sessionKey] = nowUtc;
        return true;
    }

    /// <summary>
    /// Seeds the session's window at playback start so the first progress tick
    /// fired right after PlaybackStart is suppressed (PlaybackStart already
    /// carries the position).
    /// </summary>
    public void Seed(string sessionKey, DateTime nowUtc) => _lastSentUtc[sessionKey] = nowUtc;

    /// <summary>Removes the session (call on PlaybackStopped).</summary>
    public void Remove(string sessionKey) => _lastSentUtc.TryRemove(sessionKey, out _);
}
