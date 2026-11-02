using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.WeTrakr.Api;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>
/// Last synced state of one item for one Jellyfin account. It is what lets the
/// sync upload only what changed in Jellyfin, and tell a local change apart
/// from something the sync itself just wrote.
/// </summary>
public class ItemState
{
    [JsonPropertyName("p")] public bool Played { get; set; }
    [JsonPropertyName("lp")] public long LastPlayedTicks { get; set; }
    [JsonPropertyName("pc")] public int PlayCount { get; set; }
    [JsonPropertyName("f")] public bool Favorite { get; set; }
    [JsonPropertyName("r")] public double? Rating { get; set; }
    [JsonPropertyName("pos")] public long Position { get; set; }

    /// <summary>The resume point was written by the sync (it comes from WeTrakr).</summary>
    [JsonPropertyName("wpos")] public bool PositionFromWeTrakr { get; set; }

    /// <summary>Unmarked in Jellyfin on purpose: WeTrakr's watched state does not mark it again.</summary>
    [JsonPropertyName("unw")] public bool UnwatchedInJellyfin { get; set; }
}

public class UserSyncState
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>First complete Jellyfin → WeTrakr import done.</summary>
    [JsonPropertyName("initial_done")] public bool InitialDone { get; set; }

    /// <summary>WeTrakr → Jellyfin snapshot done; from then on `changes` is read.</summary>
    [JsonPropertyName("snapshot_done")] public bool SnapshotDone { get; set; }

    [JsonPropertyName("cursor_since")] public DateTime? CursorSince { get; set; }

    /// <summary>Last "Sync now" from the WeTrakr website already served.</summary>
    [JsonPropertyName("sync_request_seen")] public DateTime? SyncRequestSeen { get; set; }

    /// <summary>Keyed by Jellyfin item id (GUID "N").</summary>
    [JsonPropertyName("items")] public Dictionary<string, ItemState> Items { get; set; } = new();

    /// <summary>Titles in WeTrakr's Planning (key "movie:tmdb:123").</summary>
    [JsonPropertyName("planning")] public HashSet<string> Planning { get; set; } = new();

    [JsonPropertyName("playlist_id")] public Guid? PlaylistId { get; set; }

    /// <summary>Items the sync put in the playlist (only those are ever removed).</summary>
    [JsonPropertyName("playlist_items")] public HashSet<Guid> PlaylistItems { get; set; } = new();

    /// <summary>Account sync settings as they came in the last /status or /changes. null = not read yet.</summary>
    [JsonPropertyName("settings")] public SyncSettings? Settings { get; set; }

    [JsonPropertyName("max_batch")] public int MaxBatch { get; set; } = 1000;

    public ItemState Get(string id)
    {
        if (!Items.TryGetValue(id, out var s))
        {
            s = new ItemState();
            Items[id] = s;
        }
        return s;
    }
}

/// <summary>
/// Sync state on disk, one JSON file per Jellyfin account in the plugin's data
/// folder. Kept out of the plugin XML so a pass does not rewrite it.
/// </summary>
public static class SyncStateStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string PathFor(string jellyfinUserId)
    {
        var dir = Plugin.Instance!.DataFolderPath;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "state-" + jellyfinUserId + ".json");
    }

    public static UserSyncState Load(string jellyfinUserId)
    {
        lock (Gate)
        {
            var path = PathFor(jellyfinUserId);
            if (!File.Exists(path)) return new UserSyncState();
            try
            {
                return JsonSerializer.Deserialize<UserSyncState>(File.ReadAllText(path), Json) ?? new UserSyncState();
            }
            catch (Exception)
            {
                // An unreadable state means starting over: the import and the
                // snapshot are both idempotent.
                return new UserSyncState();
            }
        }
    }

    /// <summary>Atomic write: temp file, then rename.</summary>
    public static void Save(string jellyfinUserId, UserSyncState state)
    {
        lock (Gate)
        {
            var path = PathFor(jellyfinUserId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public static void Reset(string jellyfinUserId)
    {
        lock (Gate)
        {
            var path = PathFor(jellyfinUserId);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
