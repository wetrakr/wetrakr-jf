using System.Collections.Concurrent;
using Jellyfin.Plugin.WeTrakr.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>
/// Decides WHEN each linked account syncs, spending as few requests to WeTrakr
/// as possible (same rules as the official Emby plugin):
///
///   - Jellyfin → WeTrakr is never polled: the plugin learns about local
///     changes from the events. When the scrobbler leaves a notice
///     (SyncSignals), LocalDebounce later the diff of those items is uploaded
///     (Local mode; zero requests when there is nothing to upload).
///   - WeTrakr → Jellyfin has to be asked (WeTrakr cannot call into the user's
///     home), so it is asked when it matters: when a linked account becomes
///     active in Jellyfin (if the last check is older than ActiveStale), every
///     ActiveInterval while it stays active, and at rest with a heartbeat that
///     doubles after every empty check, from MinInterval up to
///     MaxCheckIntervalHours. A check is ONE request (/changes answers
///     `unchanged`). A "Sync now" pressed on the WeTrakr website is seen on that
///     check and turns it into a full pass.
///   - The complete pass (reconciliation) is WeTrakrSyncTask, once a day.
/// </summary>
public class SyncScheduler : IHostedService, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ActiveStale = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ActiveCoalesce = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LocalDebounce = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupJitterMax = TimeSpan.FromMinutes(5);

    private sealed class UserClock
    {
        public DateTime LastCheckAt;
        public DateTime NextCheckAt;
        public TimeSpan Interval = MinInterval;
        public DateTime ActiveUntil;
    }

    private readonly ConcurrentDictionary<string, UserClock> _clocks = new();
    private readonly ISessionManager _sessions;
    private readonly SyncEngine _engine;
    private readonly ILogger<SyncScheduler> _logger;
    private readonly Random _random = new();
    private Timer? _timer;
    private int _ticking;

    public SyncScheduler(ISessionManager sessions, SyncEngine engine, ILogger<SyncScheduler> logger)
    {
        _sessions = sessions;
        _engine = engine;
        _logger = logger;
    }

    private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // On startup: one check with jitter (so servers do not all ask at once
        // after an update), and only if the last one is old enough.
        var now = DateTime.UtcNow;
        foreach (var link in Config?.SyncLinks() ?? Enumerable.Empty<UserLink>())
        {
            var clock = ClockFor(link.JellyfinUserId);
            var last = link.LastSyncAt ?? DateTime.MinValue;
            clock.LastCheckAt = last;
            clock.NextCheckAt = now - last >= MinInterval
                ? now + TimeSpan.FromSeconds(_random.Next(30, (int)StartupJitterMax.TotalSeconds))
                : last + MinInterval;
        }

        _sessions.SessionStarted += OnSessionActivity;
        _sessions.SessionActivity += OnSessionActivity;
        _sessions.PlaybackStart += OnPlaybackStart;
        _timer = new Timer(OnTick, null, Tick, Tick);
        _logger.LogInformation("[WeTrakr] Library sync scheduler started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.SessionStarted -= OnSessionActivity;
        _sessions.SessionActivity -= OnSessionActivity;
        _sessions.PlaybackStart -= OnPlaybackStart;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        GC.SuppressFinalize(this);
    }

    private UserClock ClockFor(string userId) => _clocks.GetOrAdd(userId.ToLowerInvariant(), _ => new UserClock());

    /// <summary>Next planned check of an account (for the settings page).</summary>
    public DateTime? NextCheckFor(string userId)
        => _clocks.TryGetValue(userId.ToLowerInvariant(), out var c) ? c.NextCheckAt : null;

    /// <summary>A linked account just started using Jellyfin, or is using it.</summary>
    public void NoteActive(Guid userId)
    {
        var id = userId.ToString("N");
        if (Config?.FindLink(id) == null) return;
        var clock = ClockFor(id);
        var now = DateTime.UtcNow;
        clock.ActiveUntil = now + ActiveWindow;
        // Becoming active with an old check: soon, but not right away, to merge
        // the notices of one session (login + browsing).
        if (now - clock.LastCheckAt >= ActiveStale && clock.NextCheckAt > now + ActiveCoalesce)
        {
            clock.NextCheckAt = now + ActiveCoalesce;
        }
    }

    private void OnSessionActivity(object? sender, SessionEventArgs e)
    {
        try
        {
            var userId = e?.SessionInfo?.UserId ?? Guid.Empty;
            if (userId != Guid.Empty) NoteActive(userId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WeTrakr] Session activity ignored");
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            foreach (var u in e.Users ?? new List<JfUser>()) NoteActive(u.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WeTrakr] Playback start ignored");
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        Task.Run(async () =>
        {
            try { await TickAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "[WeTrakr] Sync scheduler tick failed"); }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        });
    }

    private async Task TickAsync()
    {
        var cfg = Config;
        if (cfg == null || (!cfg.SyncToWeTrakr && !cfg.SyncFromWeTrakr)) return;
        var maxInterval = TimeSpan.FromHours(Math.Max(1, cfg.MaxCheckIntervalHours));

        foreach (var link in cfg.SyncLinks().ToList())
        {
            var now = DateTime.UtcNow;
            var userId = link.JellyfinUserId.ToLowerInvariant();
            var clock = ClockFor(userId);

            // Pending local changes: upload (and playlist) without asking anything.
            var dirtySince = SyncSignals.DirtySince(userId);
            if (dirtySince != null && now - dirtySince.Value >= LocalDebounce)
            {
                var items = SyncSignals.Take(userId, dirtySince.Value);
                var local = await _engine.Run(link, SyncEngine.Mode.Local, items, CancellationToken.None).ConfigureAwait(false);
                // Busy with another pass, or it failed (network, account
                // disabled on WeTrakr...): the items go back for a later tick.
                if (local == null || local.Error != null) foreach (var id in items) SyncSignals.MarkDirty(userId, id);
            }

            // Without the WeTrakr → Jellyfin direction there is nothing to ask.
            if (!cfg.SyncFromWeTrakr) continue;
            if (clock.NextCheckAt == default) clock.NextCheckAt = now;
            if (now < clock.NextCheckAt) continue;

            var result = await _engine.Run(link, SyncEngine.Mode.Check, null, CancellationToken.None).ConfigureAwait(false);
            if (result == null)
            {
                // Another sync was running: try again on the next tick.
                clock.NextCheckAt = now + Tick;
                continue;
            }

            clock.LastCheckAt = now;
            if (result.Error != null)
            {
                // Network or API error: neither punish nor insist.
                clock.Interval = TimeSpan.FromTicks(Math.Min(Math.Max(clock.Interval.Ticks, MinInterval.Ticks) * 2, maxInterval.Ticks));
            }
            else if (result.Changed)
            {
                clock.Interval = MinInterval;
            }
            else
            {
                clock.Interval = TimeSpan.FromTicks(Math.Min(clock.Interval.Ticks * 2, maxInterval.Ticks));
            }

            var active = clock.ActiveUntil > now;
            var wait = active && clock.Interval > ActiveInterval ? ActiveInterval : clock.Interval;
            clock.NextCheckAt = now + wait;
        }
    }
}
