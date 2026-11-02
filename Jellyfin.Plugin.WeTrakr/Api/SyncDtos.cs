using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.WeTrakr.Api;

// Contract of /integrations/jellyfin/* in wetrakr-api. Same shapes as the
// official Emby plugin and the Kodi add-on: the three share one controller.

public class ExtIds
{
    [JsonPropertyName("tmdb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Tmdb { get; set; }

    [JsonPropertyName("imdb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Imdb { get; set; }

    [JsonPropertyName("tvdb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Tvdb { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Tmdb == null && string.IsNullOrEmpty(Imdb) && Tvdb == null;
}

/// <summary>What the WeTrakr account lets the sync do. Decided on WeTrakr, read on every /changes.</summary>
public class SyncSettings
{
    [JsonPropertyName("import_watched")] public bool ImportWatched { get; set; } = true;
    [JsonPropertyName("import_progress")] public bool ImportProgress { get; set; } = true;
    [JsonPropertyName("import_ratings")] public bool ImportRatings { get; set; } = true;
    [JsonPropertyName("import_favorites")] public bool ImportFavorites { get; set; } = true;
    [JsonPropertyName("push_watched")] public bool PushWatched { get; set; } = true;
    [JsonPropertyName("push_progress")] public bool PushProgress { get; set; } = true;
    [JsonPropertyName("push_ratings")] public bool PushRatings { get; set; } = true;
    [JsonPropertyName("push_favorites")] public bool PushFavorites { get; set; } = true;
    [JsonPropertyName("push_planning")] public bool PushPlanning { get; set; } = true;
}

public class StatusSettings
{
    [JsonPropertyName("sync")] public SyncSettings? Sync { get; set; }
}

public class SyncStatusResponse
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("settings")] public StatusSettings? Settings { get; set; }
    [JsonPropertyName("sync_requested_at")] public DateTime? SyncRequestedAt { get; set; }
    [JsonPropertyName("server_time")] public DateTime ServerTime { get; set; }
    [JsonPropertyName("max_batch")] public int? MaxBatch { get; set; }
}

/// <summary>One movie, episode or series of the Jellyfin library, sent to WeTrakr.</summary>
public class ImportItem
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("ids")] public ExtIds Ids { get; set; } = new();

    [JsonPropertyName("series_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExtIds? SeriesIds { get; set; }

    [JsonPropertyName("season")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Season { get; set; }

    [JsonPropertyName("episode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Episode { get; set; }

    [JsonPropertyName("title")] public string? Title { get; set; }

    [JsonPropertyName("series_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeriesTitle { get; set; }

    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Year { get; set; }

    [JsonPropertyName("played")] public bool Played { get; set; }
    [JsonPropertyName("new_play")] public bool NewPlay { get; set; }
    [JsonPropertyName("play_count")] public int PlayCount { get; set; }

    [JsonPropertyName("last_played_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastPlayedAt { get; set; }

    [JsonPropertyName("position_ticks")] public long PositionTicks { get; set; }
    [JsonPropertyName("runtime_ticks")] public long RuntimeTicks { get; set; }

    /// <summary>null = not sent (on an incremental pass: unchanged).</summary>
    [JsonPropertyName("is_favorite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFavorite { get; set; }

    /// <summary>Only sent when RatingSet: null then means "rating removed".</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    [JsonIgnore] public bool RatingSet { get; set; }

    /// <summary>Jellyfin item id, to update the local state once the batch is in.</summary>
    [JsonIgnore] public Guid ItemId { get; set; }
}

public class ServerInfoPayload
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public class ImportBatch
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; set; } = "initial";
    [JsonPropertyName("final")] public bool Final { get; set; }
    [JsonPropertyName("server")] public ServerInfoPayload? Server { get; set; }
    [JsonPropertyName("items")] public List<Dictionary<string, object?>> Items { get; set; } = new();
}

public class ImportAccepted
{
    [JsonPropertyName("job_id")] public string JobId { get; set; } = string.Empty;
    [JsonPropertyName("batch")] public int Batch { get; set; }
    [JsonPropertyName("accepted")] public int Accepted { get; set; }
    [JsonPropertyName("rejected")] public int Rejected { get; set; }

    /// <summary>Only when the API waited for the batch (200). With 202 they are empty and the job is polled.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("batches_done")] public int BatchesDone { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public class ImportJobStatus
{
    [JsonPropertyName("job_id")] public string? JobId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("batches_received")] public int BatchesReceived { get; set; }
    [JsonPropertyName("batches_done")] public int BatchesDone { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public class WatchedState
{
    [JsonPropertyName("is_watched")] public bool IsWatched { get; set; }
    [JsonPropertyName("play_count")] public int PlayCount { get; set; }
    [JsonPropertyName("last_watched_at")] public DateTime? LastWatchedAt { get; set; }
    [JsonPropertyName("unknown")] public bool Unknown { get; set; }
}

/// <summary>Current state of a title on WeTrakr (snapshot / changes).</summary>
public class TitleRow
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("ids")] public ExtIds? Ids { get; set; }
    [JsonPropertyName("series_ids")] public ExtIds? SeriesIds { get; set; }
    [JsonPropertyName("season")] public int? Season { get; set; }
    [JsonPropertyName("episode")] public int? Episode { get; set; }
    [JsonPropertyName("watched")] public WatchedState? Watched { get; set; }
    [JsonPropertyName("explicit_unwatch")] public bool ExplicitUnwatch { get; set; }
    [JsonPropertyName("planning")] public bool Planning { get; set; }
    [JsonPropertyName("rating")] public double? Rating { get; set; }
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
}

public class InProgressRow
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("ids")] public ExtIds? Ids { get; set; }
    [JsonPropertyName("series_ids")] public ExtIds? SeriesIds { get; set; }
    [JsonPropertyName("season")] public int? Season { get; set; }
    [JsonPropertyName("episode")] public int? Episode { get; set; }
    [JsonPropertyName("percent")] public double Percent { get; set; }
    [JsonPropertyName("position_ms")] public long? PositionMs { get; set; }
    [JsonPropertyName("updated_at")] public DateTime? UpdatedAt { get; set; }
}

public class SnapshotPage
{
    [JsonPropertyName("section")] public string? Section { get; set; }
    [JsonPropertyName("titles")] public List<TitleRow> Titles { get; set; } = new();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
    [JsonPropertyName("cursor_since")] public DateTime? CursorSince { get; set; }
}

public class ChangesPage
{
    [JsonPropertyName("since")] public DateTime Since { get; set; }
    [JsonPropertyName("next_since")] public DateTime NextSince { get; set; }
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }

    /// <summary>Nothing changed since `since`: titles and in_progress are empty and do
    /// NOT mean "nothing is in progress any more". Only the cursor moves.</summary>
    [JsonPropertyName("unchanged")] public bool Unchanged { get; set; }

    [JsonPropertyName("titles")] public List<TitleRow> Titles { get; set; } = new();
    [JsonPropertyName("in_progress")] public List<InProgressRow>? InProgress { get; set; }

    /// <summary>Account sync settings, on every response: no /status call per pass.</summary>
    [JsonPropertyName("settings")] public StatusSettings? Settings { get; set; }
    [JsonPropertyName("max_batch")] public int? MaxBatch { get; set; }

    /// <summary>"Sync now" pressed on the WeTrakr website, when there is one.</summary>
    [JsonPropertyName("sync_requested_at")] public DateTime? SyncRequestedAt { get; set; }
}

public class ApiError
{
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}
