using Jellyfin.Plugin.WeTrakr.Api;
using Jellyfin.Plugin.WeTrakr.Configuration;
using Jellyfin.Plugin.WeTrakr.Sync;
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
///   PlaybackStart of a different item on a session whose previous item never
///                      reported a stop -> synthetic "PlaybackStop" for that
///                      previous item at its last known position (see
///                      SessionItemTracker)
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
///   UserDataSaved with SaveReason.PlaybackProgress, when Jellyfin flips the
///                       item to played mid-playback -> "ItemMarkedPlayed"
///                       (see ProgressPlayedTracker)
///
/// Only Movie and Episode items are dispatched; everything else is ignored,
/// and so is anything in a library excluded in the plugin settings.
/// Failures are logged and swallowed — scrobble must never break playback.
///
/// Library sync (0.2.0+): every user-data change that is not the sync's own
/// (SaveReason.Import) leaves a notice in SyncSignals, so the scheduler uploads
/// it even when the live event is off or did not get through; and every
/// watched mark that did get through is recorded in LiveSentRegistry, so the
/// sync does not upload it a second time.
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
    private readonly ProgressPlayedTracker _progressPlayed;
    private readonly SessionItemTracker _sessionItems;
    private readonly ILibraryManager _library;
    private readonly ILogger<ScrobbleManager> _logger;

    public ScrobbleManager(
        ISessionManager sessions,
        IUserDataManager userData,
        WeTrakrClient client,
        PayloadBuilder builder,
        PauseStateTracker paused,
        ProgressThrottle progress,
        PlayedStateTracker playedState,
        ProgressPlayedTracker progressPlayed,
        SessionItemTracker sessionItems,
        ILibraryManager library,
        ILogger<ScrobbleManager> logger)
    {
        _sessions = sessions;
        _userData = userData;
        _client = client;
        _builder = builder;
        _paused = paused;
        _progress = progress;
        _playedState = playedState;
        _progressPlayed = progressPlayed;
        _sessionItems = sessionItems;
        _library = library;
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
        var now = DateTime.UtcNow;
        _progress.Seed(SessionKey(e), now);

        if (e.Item != null && e.Session != null)
        {
            var previous = _sessionItems.Begin(e.Session.Id, e.Item, e.Session, SessionKey(e), e.PlaybackPositionTicks ?? 0, now);
            if (previous != null)
            {
                // The client moved on without stopping the previous item. Close
                // it here, as the real stop would have, or it stays "playing"
                // on WeTrakr and never gets its watched mark.
                _paused.Remove(previous.PlayKey);
                _progress.Remove(previous.PlayKey);
                _logger.LogDebug("[WeTrakr] {Item} replaced without a stop on session {Session}; sending it", previous.Item.Name, e.Session.Id);
                _ = DispatchAsync(previous.Item, previous.Session, "PlaybackStop", previous.PositionTicks, isPaused: false, played: false);
            }
        }

        _ = DispatchAsync(e, "PlaybackStart", e.IsPaused, played: false);
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var key = SessionKey(e);
        _paused.Remove(key);
        _progress.Remove(key);
        if (e.Item != null && e.Session != null)
        {
            _sessionItems.End(e.Session.Id, e.Item.Id);
        }

        var played = e.PlayedToCompletion;
        var item = e.Item;
        var userId = e.Session?.UserId ?? Guid.Empty;
        _ = DispatchAsync(e, "PlaybackStop", isPaused: false, played: played).ContinueWith(
            t =>
            {
                // Received as watched: the sync must not upload it again as a new play.
                if (t.Result && played && item != null && userId != Guid.Empty)
                {
                    LiveSentRegistry.MarkPlayed(userId.ToString("N"), item.Id);
                }
            },
            TaskScheduler.Default);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var key = SessionKey(e);
        var wasPaused = _paused.WasPaused(key);

        // Every tick refreshes the position, including the ones the throttle
        // drops below: a synthetic stop needs where the user really left off.
        if (e.Item != null && e.Session != null)
        {
            _sessionItems.Update(e.Session.Id, e.Item, e.PlaybackPositionTicks, DateTime.UtcNow);
        }

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

    private Task<bool> DispatchAsync(PlaybackProgressEventArgs e, string eventName, bool isPaused, bool played)
        => DispatchAsync(e.Item, e.Session, eventName, e.PlaybackPositionTicks ?? 0, isPaused, played);

    /// <summary>Returns whether WeTrakr received the event.</summary>
    private async Task<bool> DispatchAsync(BaseItem? item, SessionInfo? session, string eventName, long positionTicks, bool isPaused, bool played)
    {
        try
        {
            if (!ShouldDispatch(item)) return false;
            if (session == null) return false;

            var config = Plugin.Instance?.Configuration;
            if (config == null) return false;
            if (!config.ScrobblePlaying) return false;                 // user disabled

            // Filtering happens here, before anything leaves the server: an
            // account with no WeTrakr link scrobbles nowhere.
            var target = config.ResolveTarget(session.UserId.ToString("N"));
            if (target == null) return false;

            var payload = _builder.Build(eventName, item!, session, positionTicks, isPaused, played, target.IsOwner);
            return await _client.SendAsync(config, target, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeTrakr] Dispatch failed for event {Event}", eventName);
            return false;
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
            if (e.SaveReason == UserDataSaveReason.PlaybackProgress)
            {
                OnPlaybackProgressSaved(e);
                return;
            }

            // Any other change that is not the sync's own reaches the sync's
            // upload, whatever happens to the live event below (switched off,
            // or not received). Import is skipped: the sync writes with it, and
            // so do other plugins' importers (the daily full pass sees those).
            if (e.SaveReason != UserDataSaveReason.Import && ShouldDispatch(e.Item))
            {
                SyncSignals.MarkDirty(e.UserId.ToString("N"), e.Item.Id);
            }

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

    /// <summary>
    /// Forwards the played mark Jellyfin sets on its own once playback passes
    /// MaxResumePct, so the item is watched on WeTrakr even if the client never
    /// reports the stop. Runs on every progress tick: keep it cheap.
    /// </summary>
    private void OnPlaybackProgressSaved(UserDataSaveEventArgs e)
    {
        if (!ShouldDispatch(e.Item)) return;

        // Without a known runtime (or under MinResumeDuration) Jellyfin marks
        // the item played on the very first tick; that is not a finished watch.
        if ((e.Item.RunTimeTicks ?? 0) < MinRuntimeForProgressPlayed.Ticks) return;

        var key = PlayedStateTracker.KeyFor(e.UserId, e.Item.Id);
        if (!_progressPlayed.ObserveFlipToPlayed(key, e.UserData.Played, DateTime.UtcNow)) return;

        // Record it in the toggle gate as well, so a later redundant "mark as
        // played" from another plugin does not send it a second time.
        _playedState.ShouldDispatch(key, true, DateTime.UtcNow);

        _ = DispatchUserDataAsync(e, "ItemMarkedPlayed");
    }

    private async Task DispatchUserDataAsync(UserDataSaveEventArgs e, string eventName)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return;

            if (eventName == "ItemMarkedPlayed" && !config.ScrobbleWatched) return;
            if (eventName == "UserDataSaved" && !config.ScrobbleRatings) return;

            var target = config.ResolveTarget(e.UserId.ToString("N"));
            if (target == null) return;

            // Read username via the ABI-stable UserDataSaveEventArgs.User
            // property if present. On older/newer Jellyfin that renamed the
            // User entity namespace this reflection keeps us safe.
            var userName = TryGetUserName(e);

            var payload = _builder.BuildUserData(eventName, e.Item, e.UserData, e.UserId, userName, e.SaveReason.ToString(), target.IsOwner);
            var sent = await _client.SendAsync(config, target, payload, CancellationToken.None);
            if (eventName != "ItemMarkedPlayed") return;

            var userId = e.UserId.ToString("N");
            if (!sent)
            {
                // Not received: let the next mark with this value through instead
                // of taking it for a redundant re-mark, and leave the change to
                // the sync's upload meanwhile.
                _playedState.Forget(PlayedStateTracker.KeyFor(e.UserId, e.Item.Id), payload.Played);
                SyncSignals.MarkDirty(userId, e.Item.Id);
            }
            else if (payload.Played)
            {
                // Received as watched: the sync must not upload it again as a new play.
                LiveSentRegistry.MarkPlayed(userId, e.Item.Id);
            }
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

    /// <summary>Jellyfin's default MinResumeDurationSeconds.</summary>
    private static readonly TimeSpan MinRuntimeForProgressPlayed = TimeSpan.FromMinutes(5);

    private bool ShouldDispatch(BaseItem? item)
    {
        if (item == null) return false;
        if (item is not Movie && item is not Episode) return false;
        return !LibraryFilter.Current(_library).IsExcluded(item);
    }

    private static string SessionKey(PlaybackProgressEventArgs e)
        => e.PlaySessionId ?? e.Session?.Id ?? string.Empty;
}
