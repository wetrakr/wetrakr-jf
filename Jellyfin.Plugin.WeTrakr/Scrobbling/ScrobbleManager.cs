using Jellyfin.Plugin.WeTrakr.Api;
using Jellyfin.Plugin.WeTrakr.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Subscribes to ISessionManager playback events and dispatches them to the
/// WeTrakr API. Runs as an IHostedService — Jellyfin lifecycle-manages it.
///
/// Event translation:
///   PlaybackStart   -> "PlaybackStart"
///   PlaybackStopped -> "PlaybackStop"
///   PlaybackProgress with IsPaused transition:
///       false -> true : "PlaybackPause"
///       true  -> false: "PlaybackUnpause"
///       no change     : "PlaybackProgress" (throttled to at most one per
///                       ProgressThrottle.MinInterval per session — Jellyfin
///                       fires progress every few seconds and forwarding each
///                       one floods the WeTrakr API)
///
///   UserDataSaved with SaveReason.TogglePlayed -> "ItemMarkedPlayed", but only
///                       when the played flag actually flips (see
///                       PlayedStateTracker — Jellyfin raises the event even
///                       when the value is unchanged, so library-wide re-marks
///                       from other plugins would flood the API)
///
/// Only Movie and Episode items are dispatched; everything else is ignored.
/// Failures are logged and swallowed — scrobble must never break playback.
/// </summary>
public class ScrobbleManager : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly IUserDataManager _userData;
    private readonly WeTrakrClient _client;
    private readonly PayloadBuilder _builder;
    private readonly PauseStateTracker _paused;
    private readonly ProgressThrottle _progress;
    private readonly PlayedStateTracker _playedState;
    private readonly ILogger<ScrobbleManager> _logger;

    public ScrobbleManager(
        ISessionManager sessions,
        IUserDataManager userData,
        WeTrakrClient client,
        PayloadBuilder builder,
        PauseStateTracker paused,
        ProgressThrottle progress,
        PlayedStateTracker playedState,
        ILogger<ScrobbleManager> logger)
    {
        _sessions = sessions;
        _userData = userData;
        _client = client;
        _builder = builder;
        _paused = paused;
        _progress = progress;
        _playedState = playedState;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart    += OnPlaybackStart;
        _sessions.PlaybackProgress += OnPlaybackProgress;
        _sessions.PlaybackStopped  += OnPlaybackStopped;
        _userData.UserDataSaved    += OnUserDataSaved;
        _logger.LogInformation("[WeTrakr] ScrobbleManager started: subscribed to playback + user-data events.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart    -= OnPlaybackStart;
        _sessions.PlaybackProgress -= OnPlaybackProgress;
        _sessions.PlaybackStopped  -= OnPlaybackStopped;
        _userData.UserDataSaved    -= OnUserDataSaved;
        _logger.LogInformation("[WeTrakr] ScrobbleManager stopped.");
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        // Seed the throttle so the first progress tick right after start (which
        // carries the same position PlaybackStart already sent) is suppressed.
        _progress.Seed(SessionKey(e), DateTime.UtcNow);
        _ = DispatchAsync(e, "PlaybackStart", e.IsPaused, played: false);
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var key = SessionKey(e);
        _paused.Remove(key);
        _progress.Remove(key);
        _ = DispatchAsync(e, "PlaybackStop", isPaused: false, played: e.PlayedToCompletion);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var key = SessionKey(e);
        var wasPaused = _paused.WasPaused(key);

        string eventName;
        if (e.IsPaused && !wasPaused) eventName = "PlaybackPause";
        else if (!e.IsPaused && wasPaused) eventName = "PlaybackUnpause";
        else eventName = "PlaybackProgress";

        _paused.Set(key, e.IsPaused);

        // Pause/unpause transitions are meaningful and rare — always dispatch.
        // A plain progress tick only goes through once per MinInterval per
        // session; the rest are dropped here, before any HTTP call.
        if (eventName == "PlaybackProgress" && !_progress.ShouldDispatch(key, DateTime.UtcNow))
        {
            return;
        }

        _ = DispatchAsync(e, eventName, e.IsPaused, played: false);
    }

    private async Task DispatchAsync(PlaybackProgressEventArgs e, string eventName, bool isPaused, bool played)
    {
        try
        {
            if (!ShouldDispatch(e.Item)) return;

            var session = e.Session;
            if (session == null) return;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return;
            if (!config.ScrobblePlaying) return;                       // user disabled

            // Filter out excluded libraries
            if (config.ExcludedLibraries.Any(directory => e.Item.Path.Contains(directory, StringComparison.OrdinalIgnoreCase))) return;

            // Filtering happens here, before anything leaves the server: an
            // account with no WeTrakr link scrobbles nowhere.
            var target = config.ResolveTarget(session.UserId.ToString("N"));
            if (target == null) return;

            var payload = _builder.Build(eventName, e.Item, session, e.PlaybackPositionTicks ?? 0, isPaused, played, target.IsOwner);
            await _client.SendAsync(config, target, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeTrakr] Dispatch failed for event {Event}", eventName);
        }
    }

    // --- UserData events (mark watched, toggle favorite, rating) ---

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // Everything wrapped in try/catch — this handler runs on the HTTP
        // request thread of Jellyfin's own FavoriteItems/PlayedItems
        // endpoint; if it throws, the original request 500s for the user.
        try
        {
            if (e.SaveReason != UserDataSaveReason.TogglePlayed
                && e.SaveReason != UserDataSaveReason.UpdateUserRating)
            {
                return;
            }

            if (!ShouldDispatch(e.Item)) return;

            var eventName = e.SaveReason == UserDataSaveReason.TogglePlayed
                ? "ItemMarkedPlayed"
                : "UserDataSaved";

            // A re-mark that does not flip the played flag carries no new
            // information. Jellyfin raises TogglePlayed even when the value is
            // unchanged, so a plugin that re-applies an external history over
            // the whole library (a Trakt sync task, say) would otherwise have
            // us forward hundreds of identical events on every single run.
            if (eventName == "ItemMarkedPlayed"
                && !_playedState.ShouldDispatch(
                        PlayedStateTracker.KeyFor(e.UserId, e.Item.Id),
                        e.UserData.Played,
                        DateTime.UtcNow))
            {
                return;
            }

            _ = DispatchUserDataAsync(e, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeTrakr] OnUserDataSaved sync path threw");
        }
    }

    private async Task DispatchUserDataAsync(UserDataSaveEventArgs e, string eventName)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return;

            if (eventName == "ItemMarkedPlayed" && !config.ScrobbleWatched) return;
            if (eventName == "UserDataSaved" && !config.ScrobbleRatings) return;

            // Filter out excluded libraries
            if (config.ExcludedLibraries.Any(directory => e.Item.Path.Contains(directory, StringComparison.OrdinalIgnoreCase))) return;

            var target = config.ResolveTarget(e.UserId.ToString("N"));
            if (target == null) return;

            // Read username via the ABI-stable UserDataSaveEventArgs.User
            // property if present. On older/newer Jellyfin that renamed the
            // User entity namespace this reflection keeps us safe.
            var userName = TryGetUserName(e);

            var payload = _builder.BuildUserData(eventName, e.Item, e.UserData, e.UserId, userName, e.SaveReason.ToString(), target.IsOwner);
            await _client.SendAsync(config, target, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeTrakr] UserDataSaved dispatch failed for event {Event}", eventName);
        }
    }

    private static string? TryGetUserName(UserDataSaveEventArgs e)
    {
        try
        {
            var userProp = e.GetType().GetProperty("User");
            var user = userProp?.GetValue(e);
            if (user == null) return null;

            var nameProp = user.GetType().GetProperty("Username") ?? user.GetType().GetProperty("Name");
            return nameProp?.GetValue(user) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldDispatch(BaseItem? item)
    {
        if (item == null) return false;
        return item is Movie || item is Episode;
    }

    private static string SessionKey(PlaybackProgressEventArgs e)
        => e.PlaySessionId ?? e.Session?.Id ?? string.Empty;
}
