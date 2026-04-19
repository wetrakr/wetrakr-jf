using System.Collections.Concurrent;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Tracks the IsPaused state per playback session so we can turn transitions
/// on PlaybackProgress into synthetic PlaybackPause / PlaybackUnpause events.
/// Jellyfin's ISessionManager does not fire dedicated pause/unpause events.
/// </summary>
public class PauseStateTracker
{
    private readonly ConcurrentDictionary<string, bool> _state = new();

    /// <summary>Returns true if the previous recorded state was paused.</summary>
    public bool WasPaused(string sessionKey) => _state.TryGetValue(sessionKey, out var p) && p;

    /// <summary>Records the current IsPaused value for the session.</summary>
    public void Set(string sessionKey, bool isPaused) => _state[sessionKey] = isPaused;

    /// <summary>Removes the session (call on PlaybackStopped).</summary>
    public void Remove(string sessionKey) => _state.TryRemove(sessionKey, out _);
}
