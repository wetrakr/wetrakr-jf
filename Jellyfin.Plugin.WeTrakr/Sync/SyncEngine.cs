using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WeTrakr.Api;
using Jellyfin.Plugin.WeTrakr.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>
/// One sync pass for one linked Jellyfin account. Same design as the official
/// Emby plugin and the Kodi add-on (the three talk to the same API):
///
///   1. Upload (Jellyfin → WeTrakr). The first time, everything watched, in
///      progress or favorite; afterwards only what changed since the last pass,
///      compared with the local state. Never sends "unwatched": only the live
///      event does that, and WeTrakr decides what to do with the history.
///   2. Download (WeTrakr → Jellyfin). The first time the full snapshot;
///      afterwards the journal changes since the cursor. STATES are applied,
///      with SaveUserData(..., Import): the scrobbler ignores Import, so none of
///      this comes back to WeTrakr.
///   3. Resume points and the Planning playlist.
///
/// Upload goes before download: otherwise a change made in Jellyfin since the
/// last pass would be overwritten by WeTrakr's older state.
///
/// Three modes, to spend as few requests as possible (see SyncScheduler):
///   Check  one /changes call; if WeTrakr says `unchanged`, that is all. This
///          is what the polling runs.
///   Local  something changed in Jellyfin (the events say which items): upload
///          the diff of those items and refresh the playlist. Zero requests
///          when the diff is empty.
///   Full   everything, no shortcuts: first sync, "Sync now" and the daily pass.
/// </summary>
public class SyncEngine
{
    public enum Mode { Check, Local, Full }

    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static readonly string[] SnapshotSections = { "tracks", "ratings", "favorites" };

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IPlaylistManager _playlists;
    private readonly ISessionManager _sessions;
    private readonly IServerApplicationHost _host;
    private readonly SyncApi _api;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(ILibraryManager library, IUserManager users, IUserDataManager userData, IPlaylistManager playlists,
        ISessionManager sessions, IServerApplicationHost host, SyncApi api, ILogger<SyncEngine> logger)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _playlists = playlists;
        _sessions = sessions;
        _host = host;
        _api = api;
        _logger = logger;
    }

    public class Result
    {
        public int Uploaded { get; set; }
        public int Applied { get; set; }
        public int Progress { get; set; }
        public bool Unchanged { get; set; }
        public string? Error { get; set; }

        /// <summary>"Sync now" was pressed on the WeTrakr website (seen on /changes).</summary>
        public bool SyncRequested { get; set; }

        /// <summary>Something was uploaded or applied (for the polling backoff).</summary>
        public bool Changed => Uploaded > 0 || Applied > 0 || Progress > 0;

        public override string ToString()
        {
            if (Error != null) return Error;
            if (Unchanged && Uploaded == 0) return "nothing changed";
            return $"{Uploaded} sent to WeTrakr, {Applied} updated in Jellyfin, {Progress} resume points";
        }
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static bool AnyDirection(PluginConfiguration cfg) => cfg.SyncToWeTrakr || cfg.SyncFromWeTrakr;

    /// <summary>Full pass of every linked account (scheduled task, "Sync now" on the settings page).</summary>
    public async Task RunAll(IProgress<double>? progress, CancellationToken ct)
    {
        if (!AnyDirection(Config)) return;
        if (!await RunLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("[WeTrakr] A sync is already running");
            return;
        }
        try
        {
            var links = Config.SyncLinks().ToList();
            for (var i = 0; i < links.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await RunOne(links[i], Mode.Full, null, ct).ConfigureAwait(false);
                progress?.Report(100.0 * (i + 1) / links.Count);
            }
            Plugin.Instance!.SaveConfiguration();
        }
        finally
        {
            RunLock.Release();
        }
    }

    /// <summary>
    /// One account, in the given mode. Returns null when a sync was already
    /// running (it does not wait: the scheduler comes back on its next tick).
    /// </summary>
    public async Task<Result?> Run(UserLink link, Mode mode, IReadOnlyCollection<Guid>? dirtyItems, CancellationToken ct)
    {
        if (!AnyDirection(Config)) return null;
        if (!await RunLock.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            var result = await RunOne(link, mode, dirtyItems, ct).ConfigureAwait(false);
            // A check that finds a "Sync now" from the website turns into a full pass.
            if (mode == Mode.Check && result.SyncRequested && result.Error == null)
            {
                result = await RunOne(link, Mode.Full, null, ct).ConfigureAwait(false);
            }
            Plugin.Instance!.SaveConfiguration();
            return result;
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<Result> RunOne(UserLink link, Mode mode, IReadOnlyCollection<Guid>? dirtyItems, CancellationToken ct)
    {
        var result = new Result();
        var user = Guid.TryParse(link.JellyfinUserId, out var uid) ? _users.GetUserById(uid) : null;
        if (user == null)
        {
            result.Error = link.LastSyncResult = "Jellyfin user not found";
            return result;
        }

        var ctx = SyncContext.From(link);
        try
        {
            ctx.ServerId = _host.SystemId;
            ctx.ServerName = _host.FriendlyName;
            ctx.ServerVersion = _host.ApplicationVersionString;
        }
        catch (Exception)
        {
            // Informative only.
        }

        try
        {
            var cfg = Config;
            var state = SyncStateStore.Load(link.JellyfinUserId);

            // The settings come with every /changes; /status is only needed the
            // first time, before the snapshot (and the first upload).
            if (state.Settings == null)
            {
                var status = await _api.GetStatus(ctx, ct).ConfigureAwait(false);
                RememberSettings(state, status?.Settings, status?.MaxBatch);
                // A "Sync now" pressed before this server ever synced is served by this first pass.
                state.SyncRequestSeen = status?.SyncRequestedAt ?? state.SyncRequestSeen;
            }

            // Upload: always in Full and Local (zero requests when the diff is
            // empty); in Check only the first time, which is the heaviest one
            // and must go before the snapshot overwrites the local state.
            if (cfg.SyncToWeTrakr && (mode != Mode.Check || !state.InitialDone))
            {
                result.Uploaded = await Upload(ctx, user, state, mode, dirtyItems, Math.Max(1, Math.Min(state.MaxBatch, 1000)), ct).ConfigureAwait(false);
                SyncStateStore.Save(link.JellyfinUserId, state);
            }

            LibraryIndex? index = null;
            var sync = state.Settings ?? new SyncSettings();

            if (mode != Mode.Local && cfg.SyncFromWeTrakr)
            {
                index = LibraryIndex.Build(_library, user);
                var pull = await Pull(ctx, user, state, index, mode == Mode.Full, ct).ConfigureAwait(false);
                sync = state.Settings ?? sync;
                result.Unchanged = pull.Unchanged;
                result.Applied = pull.Applied;
                result.SyncRequested = pull.SyncRequested && mode != Mode.Full;
                // With `unchanged` the in-progress list is empty and does not mean
                // "nothing is in progress": resume points are left alone.
                if (!pull.Unchanged) result.Progress = ApplyProgress(user, state, index, sync, pull.InProgress);
            }

            // Playlist: when Planning may have changed (download with changes) or
            // what is watched in Jellyfin did (Local: the next unwatched episode moves).
            var playlistDue = mode == Mode.Full || mode == Mode.Local || result.Applied > 0;
            if (playlistDue && cfg.SyncFromWeTrakr && cfg.WatchlistPlaylist && sync.PushPlanning)
            {
                index ??= LibraryIndex.Build(_library, user);
                await SyncPlaylist(user, state, index).ConfigureAwait(false);
            }

            SyncStateStore.Save(link.JellyfinUserId, state);
            link.LastSyncAt = DateTime.UtcNow;
            link.LastSyncResult = result.ToString();
            if (mode == Mode.Full || result.Changed) _logger.LogInformation("[WeTrakr] Sync for {User} ({Mode}): {Result}", user.Username, mode, result);
            else _logger.LogDebug("[WeTrakr] Sync for {User} ({Mode}): {Result}", user.Username, mode, result);
        }
        catch (InvalidTokenException)
        {
            link.Invalid = true;
            result.Error = link.LastSyncResult = "WeTrakr rejected the link. Link the account again.";
            _logger.LogWarning("[WeTrakr] Token for {User} is no longer valid", user.Username);
        }
        catch (ApiException ex) when (ex.Error == "account_excluded")
        {
            result.Error = link.LastSyncResult = "This Jellyfin account is disabled on WeTrakr (Settings > Jellyfin).";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = link.LastSyncResult = "Sync failed: " + ex.Message;
            _logger.LogError(ex, "[WeTrakr] Sync for {User} failed", user.Username);
        }
        return result;
    }

    private static void RememberSettings(UserSyncState state, StatusSettings? settings, int? maxBatch)
    {
        if (settings?.Sync != null) state.Settings = settings.Sync;
        else state.Settings ??= new SyncSettings();
        if (maxBatch != null && maxBatch > 0) state.MaxBatch = maxBatch.Value;
    }

    // =============================================================
    // UPLOAD (Jellyfin → WeTrakr)
    // =============================================================

    private async Task<int> Upload(SyncContext ctx, JfUser user, UserSyncState state, Mode mode, IReadOnlyCollection<Guid>? dirtyItems, int maxBatch, CancellationToken ct)
    {
        var initial = !state.InitialDone;
        var filter = LibraryFilter.Current(_library);
        List<BaseItem> candidates;
        if (initial) candidates = filter.Apply(ScanInteresting(user));
        else if (mode == Mode.Local) candidates = filter.Apply(ItemsById(dirtyItems));
        else candidates = filter.Apply(ScanInteresting(user).Concat(ItemsById(KnownInteresting(state))).DistinctBy(i => i.Id));

        var userId = user.Id.ToString("N");
        var items = new List<ImportItem>();
        foreach (var item in candidates)
        {
            var data = UserData(user, item);
            if (data == null) continue;
            var it = initial ? InitialItem(item, data) : DiffItem(userId, item, data, state);
            if (it != null) items.Add(it);
        }

        if (items.Count > 0)
        {
            await SendBatches(ctx, items, initial ? "initial" : "incremental", maxBatch, ct).ConfigureAwait(false);
            foreach (var it in items)
            {
                var item = _library.GetItemById(it.ItemId);
                var data = item == null ? null : UserData(user, item);
                if (data != null) Remember(state.Get(it.ItemId.ToString("N")), data);
            }
        }

        // Whatever was looked at counts as known state, uploaded or not:
        // otherwise the next incremental pass would see it as a change.
        var uploaded = items.Select(i => i.ItemId).ToHashSet();
        foreach (var item in candidates)
        {
            if (uploaded.Contains(item.Id)) continue;
            var data = UserData(user, item);
            if (data != null) Remember(state.Get(ItemIds.Key(item)), data);
        }
        if (initial) state.InitialDone = true;
        return items.Count;
    }

    private UserItemData? UserData(JfUser user, BaseItem item)
    {
        try
        {
            return _userData.GetUserData(user, item);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What is watched, favorite, half-watched or rated: everything the sync
    /// cares about. Jellyfin cannot filter by user rating, so movies and series
    /// are read whole (a few thousand at most; the rating is checked per item);
    /// episodes, which can be tens of thousands, only through the filters.
    /// </summary>
    private List<BaseItem> ScanInteresting(JfUser user)
    {
        var byId = new Dictionary<Guid, BaseItem>();
        void Add(InternalItemsQuery q)
        {
            foreach (var it in _library.GetItemList(q)) byId[it.Id] = it;
        }

        Add(new InternalItemsQuery(user) { IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series }, Recursive = true, IsVirtualItem = false });
        var playable = new[] { BaseItemKind.Movie, BaseItemKind.Episode };
        Add(new InternalItemsQuery(user) { IncludeItemTypes = playable, Recursive = true, IsVirtualItem = false, IsPlayed = true });
        Add(new InternalItemsQuery(user) { IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series }, Recursive = true, IsVirtualItem = false, IsFavorite = true });
        Add(new InternalItemsQuery(user) { IncludeItemTypes = playable, Recursive = true, IsVirtualItem = false, IsResumable = true });
        return byId.Values.ToList();
    }

    /// <summary>
    /// Items the state remembers as watched, favorite, rated or in progress.
    /// Read on a full pass so that what stopped being any of those (unfavorited,
    /// rating removed) is noticed even though the scan no longer returns it.
    /// </summary>
    private static IEnumerable<Guid> KnownInteresting(UserSyncState state)
    {
        foreach (var kv in state.Items)
        {
            var s = kv.Value;
            if ((s.Favorite || s.Rating != null || s.Position > 0) && Guid.TryParse(kv.Key, out var id)) yield return id;
        }
    }

    private List<BaseItem> ItemsById(IEnumerable<Guid>? ids)
    {
        var list = new List<BaseItem>();
        if (ids == null) return list;
        foreach (var id in ids)
        {
            var item = _library.GetItemById(id);
            if (item is Movie || item is Episode || item is Series) list.Add(item);
        }
        return list;
    }

    private static ImportItem? BaseImportItem(BaseItem item, UserItemData data)
    {
        var it = new ImportItem
        {
            ItemId = item.Id,
            Ids = ItemIds.Ext(item),
            Title = item.Name,
            Year = item.ProductionYear,
            Played = data.Played,
            PlayCount = data.PlayCount,
            LastPlayedAt = data.LastPlayedDate,
            PositionTicks = data.PlaybackPositionTicks,
            RuntimeTicks = item.RunTimeTicks ?? 0
        };
        switch (item)
        {
            case Movie:
                it.Type = "movie";
                break;
            case Series:
                it.Type = "show";
                break;
            case Episode ep:
                it.Type = "episode";
                var series = ep.Series;
                it.SeriesIds = series != null ? ItemIds.Ext(series) : new ExtIds();
                it.SeriesTitle = series?.Name ?? ep.SeriesName;
                it.Season = ep.ParentIndexNumber;
                it.Episode = ep.IndexNumber;
                it.Year = null;
                break;
            default:
                return null;
        }

        if (it.Type == "episode" ? (it.SeriesIds!.IsEmpty && it.Ids.IsEmpty) : it.Ids.IsEmpty) return null;
        return it;
    }

    private static ImportItem? InitialItem(BaseItem item, UserItemData data)
    {
        var it = BaseImportItem(item, data);
        if (it == null) return null;
        it.IsFavorite = data.IsFavorite;
        if (data.Rating != null)
        {
            it.Rating = data.Rating;
            it.RatingSet = true;
        }
        var interesting = data.Played || data.IsFavorite || data.Rating != null || data.PlaybackPositionTicks > 0;
        return interesting ? it : null;
    }

    /// <summary>What changed compared with the last synced state, or null.</summary>
    private static ImportItem? DiffItem(string userId, BaseItem item, UserItemData data, UserSyncState state)
    {
        var it = BaseImportItem(item, data);
        if (it == null) return null;
        state.Items.TryGetValue(ItemIds.Key(item), out var st);
        st ??= new ItemState();
        var changed = false;

        var lp = Ticks(data.LastPlayedDate);
        if (data.Played && it.Type != "show")
        {
            // A new play: it became watched, or it was watched again (later date).
            var replay = st.Played && lp > st.LastPlayedTicks + TimeSpan.FromMinutes(1).Ticks;
            if (!st.Played || replay)
            {
                var since = st.LastPlayedTicks > 0 ? new DateTime(st.LastPlayedTicks, DateTimeKind.Utc) : DateTime.MinValue;
                if (!LiveSentRegistry.PlayedSince(userId, item.Id, since))
                {
                    it.NewPlay = true;
                    changed = true;
                }
            }
        }
        if (data.IsFavorite != st.Favorite)
        {
            it.IsFavorite = data.IsFavorite;
            changed = true;
        }
        if (!SameRating(data.Rating, st.Rating))
        {
            it.Rating = data.Rating;
            it.RatingSet = true;
            changed = true;
        }
        if (!data.Played && it.Type != "show" && data.PlaybackPositionTicks > 0 && data.PlaybackPositionTicks != st.Position)
        {
            changed = true;
        }
        return changed ? it : null;
    }

    /// <summary>
    /// An "unwatched" done in Jellyfin on purpose wins over WeTrakr's watched
    /// state as long as WeTrakr has no watch later than the one Jellyfin knew.
    /// </summary>
    private static bool KeepLocalUnwatch(ItemState st, WatchedState watched)
    {
        if (!st.UnwatchedInJellyfin) return false;
        var last = watched.LastWatchedAt?.ToUniversalTime().Ticks ?? 0;
        return watched.Unknown || last <= st.LastPlayedTicks;
    }

    private static bool SameRating(double? a, double? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return Math.Abs(a.Value - b.Value) < 0.01;
    }

    private static long Ticks(DateTime? date)
        => date == null ? 0 : DateTime.SpecifyKind(date.Value, DateTimeKind.Utc).Ticks;

    private static void Remember(ItemState st, UserItemData data)
    {
        // Watched → unwatched seen in Jellyfin: it was on purpose (the sync only
        // unmarks from ApplyRow, which clears the flag right after).
        if (st.Played && !data.Played) st.UnwatchedInJellyfin = true;
        if (data.Played) st.UnwatchedInJellyfin = false;
        st.Played = data.Played;
        st.LastPlayedTicks = Ticks(data.LastPlayedDate);
        st.PlayCount = data.PlayCount;
        st.Favorite = data.IsFavorite;
        st.Rating = data.Rating;
        if (st.Position != data.PlaybackPositionTicks) st.PositionFromWeTrakr = false;
        st.Position = data.PlaybackPositionTicks;
    }

    private async Task SendBatches(SyncContext ctx, List<ImportItem> items, string mode, int maxBatch, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var server = new ServerInfoPayload { Id = ctx.ServerId, Name = ctx.ServerName };
        var batches = items.Chunk(maxBatch).ToList();

        for (var b = 0; b < batches.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = new ImportBatch
            {
                SessionId = sessionId,
                Mode = mode,
                Final = b == batches.Count - 1,
                Server = server,
                Items = batches[b].Select(SyncApi.ToPayload).ToList()
            };
            var accepted = await _api.PostImport(ctx, batch, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("WeTrakr did not accept the batch");
            await WaitForBatch(ctx, accepted, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the API to finish the batch before sending the next one.</summary>
    private async Task WaitForBatch(SyncContext ctx, ImportAccepted accepted, CancellationToken ct)
    {
        // The API waits a few seconds before answering: usually the batch is
        // already done and there is nothing to poll.
        if (accepted.Status == "failed" || accepted.Status == "canceled") throw new InvalidOperationException("WeTrakr import failed: " + (accepted.Error ?? accepted.Status));
        if (accepted.BatchesDone >= accepted.Batch) return;

        var deadline = DateTime.UtcNow.AddMinutes(15);
        var delay = 1000;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 5000);
            var job = await _api.GetImportJob(ctx, accepted.JobId, ct).ConfigureAwait(false);
            if (job == null) continue;
            if (job.Status == "failed" || job.Status == "canceled") throw new InvalidOperationException("WeTrakr import failed: " + (job.Error ?? job.Status));
            if (job.BatchesDone >= accepted.Batch) return;
        }
        throw new TimeoutException("WeTrakr import took too long");
    }

    // =============================================================
    // DOWNLOAD (WeTrakr → Jellyfin)
    // =============================================================

    private sealed class PullResult
    {
        public int Applied { get; set; }
        public bool Unchanged { get; set; }
        public bool SyncRequested { get; set; }
        public List<InProgressRow> InProgress { get; set; } = new();
    }

    private async Task<PullResult> Pull(SyncContext ctx, JfUser user, UserSyncState state, LibraryIndex index, bool full, CancellationToken ct)
    {
        var res = new PullResult();
        var sync = state.Settings ?? new SyncSettings();

        if (!state.SnapshotDone || state.CursorSince == null)
        {
            DateTime? cursorSince = null;
            var planning = new HashSet<string>();
            foreach (var section in SnapshotSections)
            {
                string? cursor = null;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    var page = await _api.GetSnapshot(ctx, section, cursor, ct).ConfigureAwait(false) ?? new SnapshotPage();
                    cursorSince ??= page.CursorSince;
                    foreach (var row in page.Titles)
                    {
                        res.Applied += ApplyRow(user, state, index, sync, row, false);
                        if (row.Planning)
                        {
                            var key = LibraryIndex.TitleKey(row.Type, row.Ids);
                            if (key != null) planning.Add(key);
                        }
                    }
                    cursor = page.NextCursor;
                } while (!string.IsNullOrEmpty(cursor));
            }
            state.Planning = planning;
            state.CursorSince = cursorSince ?? DateTime.UtcNow;
            state.SnapshotDone = true;
            // After a snapshot the journal is read in full: what changed in the
            // meantime must arrive, and the in-progress list was not applied yet.
            full = true;
        }

        ChangesPage changes;
        var guard = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                changes = await _api.GetChanges(ctx, state.CursorSince!.Value, full, ct).ConfigureAwait(false) ?? new ChangesPage { Unchanged = true, NextSince = state.CursorSince!.Value };
            }
            catch (ApiException ex) when (ex.Error == "SNAPSHOT_REQUIRED")
            {
                // More than 30 days without a sync: the journal does not go that far back.
                state.SnapshotDone = false;
                state.CursorSince = null;
                return await Pull(ctx, user, state, index, true, ct).ConfigureAwait(false);
            }
            RememberSettings(state, changes.Settings, changes.MaxBatch);
            sync = state.Settings!;
            if (changes.SyncRequestedAt != null && (state.SyncRequestSeen == null || changes.SyncRequestedAt > state.SyncRequestSeen))
            {
                state.SyncRequestSeen = changes.SyncRequestedAt;
                res.SyncRequested = true;
            }
            if (changes.Unchanged)
            {
                state.CursorSince = changes.NextSince;
                res.Unchanged = true;
                return res;
            }
            foreach (var row in changes.Titles)
            {
                res.Applied += ApplyRow(user, state, index, sync, row, true);
                var key = LibraryIndex.TitleKey(row.Type, row.Ids);
                if (key == null) continue;
                if (row.Planning) state.Planning.Add(key);
                else state.Planning.Remove(key);
            }
            state.CursorSince = changes.NextSince;
            res.InProgress = changes.InProgress ?? new List<InProgressRow>();
            // The next pages of a read with changes are read in full.
            full = true;
        } while (changes.HasMore && ++guard < 100);

        return res;
    }

    /// <summary>Applies the state of one WeTrakr title to its Jellyfin items.</summary>
    private int ApplyRow(JfUser user, UserSyncState state, LibraryIndex index, SyncSettings sync, TitleRow row, bool fromChanges)
    {
        var applied = 0;
        foreach (var item in index.Find(row.Type, row.Ids, row.SeriesIds, row.Season, row.Episode))
        {
            var data = UserData(user, item);
            if (data == null) continue;
            var st = state.Get(ItemIds.Key(item));
            var dirty = false;
            var unwatchFromWeTrakr = false;

            // Unmarked in Jellyfin since the last pass (to watch it again):
            // remembered so WeTrakr's state does not mark it again.
            if (st.Played && !data.Played) st.UnwatchedInJellyfin = true;

            if (sync.PushWatched && row.Watched != null && item is not Series)
            {
                if (row.Watched.IsWatched && !data.Played && !KeepLocalUnwatch(st, row.Watched))
                {
                    data.Played = true;
                    data.PlayCount = Math.Max(Math.Max(data.PlayCount, 1), row.Watched.PlayCount);
                    if (!row.Watched.Unknown && row.Watched.LastWatchedAt != null)
                    {
                        data.LastPlayedDate = row.Watched.LastWatchedAt.Value.ToUniversalTime();
                    }
                    else
                    {
                        data.LastPlayedDate ??= DateTime.UtcNow;
                    }
                    data.PlaybackPositionTicks = 0;
                    dirty = true;
                }
                else if (fromChanges && row.ExplicitUnwatch && data.Played)
                {
                    // Only when it was not watched again in Jellyfin since the
                    // last pass: the most recent local watch wins.
                    var unchangedLocally = st.Played && Ticks(data.LastPlayedDate) <= st.LastPlayedTicks;
                    if (unchangedLocally)
                    {
                        data.Played = false;
                        dirty = true;
                        unwatchFromWeTrakr = true;
                    }
                }
            }

            if (sync.PushFavorites && data.IsFavorite != row.Favorite)
            {
                // Remove a favorite only when it was synced as one and has not
                // been touched in Jellyfin since.
                if (row.Favorite || (fromChanges && st.Favorite && data.IsFavorite))
                {
                    data.IsFavorite = row.Favorite;
                    dirty = true;
                }
            }

            if (sync.PushRatings)
            {
                if (row.Rating != null && (data.Rating == null || Math.Abs(data.Rating.Value - row.Rating.Value) >= 0.5))
                {
                    data.Rating = Math.Clamp(row.Rating.Value, 0, 10);
                    dirty = true;
                }
                else if (fromChanges && row.Rating == null && data.Rating != null && SameRating(st.Rating, data.Rating))
                {
                    data.Rating = null;
                    dirty = true;
                }
            }

            if (dirty)
            {
                _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
                applied++;
            }
            Remember(st, data);
            if (unwatchFromWeTrakr) st.UnwatchedInJellyfin = false;
        }
        return applied;
    }

    // =============================================================
    // RESUME POINTS
    // =============================================================

    private int ApplyProgress(JfUser user, UserSyncState state, LibraryIndex index, SyncSettings sync, List<InProgressRow> rows)
    {
        if (!sync.PushProgress) return 0;
        var written = 0;
        var playing = PlayingItemIds();
        var wanted = new HashSet<Guid>();

        foreach (var row in rows)
        {
            foreach (var item in index.Find(row.Type, row.Ids, row.SeriesIds, row.Season, row.Episode))
            {
                wanted.Add(item.Id);
                if (playing.Contains(item.Id)) continue;
                // Also when the title is already watched: it is a half-done
                // rewatch, and a watched item with a position shows up in
                // "Continue watching".
                var data = UserData(user, item);
                if (data == null) continue;
                var st = state.Get(ItemIds.Key(item));

                long ticks;
                if (row.PositionMs != null && row.PositionMs > 0) ticks = row.PositionMs.Value * 10000;
                else if (item.RunTimeTicks != null) ticks = (long)(item.RunTimeTicks.Value * row.Percent / 100.0);
                else continue;
                if (ticks <= 0) continue;

                // A resume point of Jellyfin's own is never overwritten.
                var ours = st.PositionFromWeTrakr && data.PlaybackPositionTicks == st.Position;
                if (data.PlaybackPositionTicks > 0 && !ours) continue;
                if (Math.Abs(data.PlaybackPositionTicks - ticks) < TimeSpan.FromSeconds(30).Ticks) continue;

                data.PlaybackPositionTicks = ticks;
                if (data.LastPlayedDate == null && row.UpdatedAt != null) data.LastPlayedDate = row.UpdatedAt.Value.ToUniversalTime();
                _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
                Remember(st, data);
                st.PositionFromWeTrakr = true;
                written++;
            }
        }

        // What the sync put there and is no longer in progress on WeTrakr leaves
        // "Continue watching" (when it was not touched in Jellyfin).
        foreach (var kv in state.Items.Where(kv => kv.Value.PositionFromWeTrakr).ToList())
        {
            if (!Guid.TryParse(kv.Key, out var id) || wanted.Contains(id) || playing.Contains(id)) continue;
            var item = index.GetById(id);
            if (item == null) continue;
            var data = UserData(user, item);
            if (data == null) continue;
            if (data.PlaybackPositionTicks == kv.Value.Position)
            {
                data.PlaybackPositionTicks = 0;
                _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
            }
            Remember(kv.Value, data);
            kv.Value.PositionFromWeTrakr = false;
        }
        return written;
    }

    private HashSet<Guid> PlayingItemIds()
    {
        var set = new HashSet<Guid>();
        try
        {
            foreach (var s in _sessions.Sessions)
            {
                var id = s?.FullNowPlayingItem?.Id ?? s?.NowPlayingItem?.Id ?? Guid.Empty;
                if (id != Guid.Empty) set.Add(id);
            }
        }
        catch (Exception)
        {
            // No sessions: nothing to protect.
        }
        return set;
    }

    // =============================================================
    // PLANNING PLAYLIST
    // =============================================================

    private async Task SyncPlaylist(JfUser user, UserSyncState state, LibraryIndex index)
    {
        var desired = new List<Guid>();
        foreach (var key in state.Planning)
        {
            var ids = LibraryIndex.ParseTitleKey(key, out var type);
            if (ids == null) continue;
            if (type == "movie")
            {
                var movie = index.Find("movie", ids, null, null, null).FirstOrDefault();
                if (movie != null) desired.Add(movie.Id);
            }
            else if (type == "show")
            {
                // For a series, its first unwatched episode (Jellyfin would expand
                // the whole series into the playlist).
                var series = index.Find("show", ids, null, null, null).FirstOrDefault();
                if (series == null) continue;
                var next = index.EpisodesOf(series.Id)
                    .Where(e => (e.ParentIndexNumber ?? 0) > 0)
                    .OrderBy(e => e.ParentIndexNumber).ThenBy(e => e.IndexNumber)
                    .FirstOrDefault(e => !(UserData(user, e)?.Played ?? false));
                if (next != null) desired.Add(next.Id);
            }
        }
        desired = desired.Distinct().ToList();
        var desiredSet = new HashSet<Guid>(desired);
        var name = Config.WatchlistPlaylistName;
        if (string.IsNullOrWhiteSpace(name)) name = "WeTrakr Watchlist";

        var playlist = state.PlaylistId != null ? _library.GetItemById(state.PlaylistId.Value) as Playlist : null;
        if (playlist == null)
        {
            state.PlaylistItems.Clear();
            if (desired.Count == 0) return;
            var created = await _playlists.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = name,
                UserId = user.Id,
                MediaType = MediaType.Video,
                ItemIdList = desired,
                Public = false
            }).ConfigureAwait(false);
            if (created != null && Guid.TryParse(created.Id, out var newId)) state.PlaylistId = newId;
            state.PlaylistItems = desiredSet;
            return;
        }

        var present = playlist.GetManageableItems()
            .Select(t => t.Item1.ItemId ?? t.Item2?.Id ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        var toAdd = desired.Where(id => !present.Contains(id)).ToList();
        if (toAdd.Count > 0)
        {
#if JF12
            await _playlists.AddItemToPlaylistAsync(playlist.Id, toAdd, null, user.Id).ConfigureAwait(false);
#else
            await _playlists.AddItemToPlaylistAsync(playlist.Id, toAdd, user.Id).ConfigureAwait(false);
#endif
        }

        // Only what the sync itself put there is ever removed.
        var toRemove = present.Where(id => state.PlaylistItems.Contains(id) && !desiredSet.Contains(id)).Select(id => id.ToString("N")).ToList();
        if (toRemove.Count > 0) await _playlists.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), toRemove).ConfigureAwait(false);

        state.PlaylistItems = desiredSet;
    }
}
