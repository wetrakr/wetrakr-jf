using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>
/// What is left out of WeTrakr: nothing in it is scrobbled or synced (not
/// uploaded, not written, not added to the playlist). Two kinds of rule, both
/// server-wide and per linked account (the account's add to the server's):
///   - libraries, by VirtualFolderInfo.ItemId;
///   - paths, a folder prefix that covers a whole library or a subfolder.
/// Resolved once per saved configuration and account, and cached.
/// </summary>
public class LibraryFilter
{
    private static readonly string[] IgnoredCollectionTypes =
        { "music", "musicvideos", "photos", "books", "audiobooks", "playlists", "boxsets", "livetv", "homevideos" };

    private static readonly object CacheLock = new();

    // Per account ("" = server-wide rules only): the filter and the rules it was built from.
    private static readonly Dictionary<string, (string Key, LibraryFilter Filter)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILibraryManager _library;
    private readonly HashSet<Guid> _excluded;
    private readonly List<string> _paths;

    private LibraryFilter(ILibraryManager library, HashSet<Guid> excluded, List<string> paths)
    {
        _library = library;
        _excluded = excluded;
        _paths = paths;
    }

    public bool IsEmpty => _excluded.Count == 0 && _paths.Count == 0;

    /// <summary>Server-wide rules only (legacy pairing, or no account at hand).</summary>
    public static LibraryFilter Current(ILibraryManager library) => Current(library, null);

    /// <summary>
    /// Filter for one Jellyfin account (GUID, any format): the server-wide rules
    /// plus those of its link. An account with no link gets the server-wide ones.
    /// </summary>
    public static LibraryFilter Current(ILibraryManager library, string? jellyfinUserId)
    {
        var cfg = Plugin.Instance?.Configuration;
        var userKey = Guid.TryParse(jellyfinUserId, out var uid) ? uid.ToString("N") : string.Empty;
        var link = userKey.Length > 0 ? cfg?.FindLink(userKey) : null;

        var ids = new List<string>();
        var paths = new List<string>();
        if (cfg?.ExcludedLibraryIds != null) ids.AddRange(cfg.ExcludedLibraryIds);
        if (cfg?.ExcludedPaths != null) paths.AddRange(cfg.ExcludedPaths);
        if (link?.ExcludedLibraryIds != null) ids.AddRange(link.ExcludedLibraryIds);
        if (link?.ExcludedPaths != null) paths.AddRange(link.ExcludedPaths);

        // The rules themselves are the cache key: a saved configuration that
        // changes them builds a new filter, one that does not reuses the old.
        var key = string.Join(",", ids) + "|" + string.Join("\n", paths);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(userKey, out var hit) && hit.Key == key) return hit.Filter;
        }

        var set = new HashSet<Guid>();
        foreach (var raw in ids)
        {
            if (Guid.TryParse(raw, out var g)) set.Add(g);
        }
        var prefixes = paths
            .Select(NormalizePath)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filter = new LibraryFilter(library, set, prefixes);
        lock (CacheLock)
        {
            Cache[userKey] = (key, filter);
        }
        return filter;
    }

    /// <summary>Libraries that make sense to exclude (movies, shows, mixed), for the settings page.</summary>
    public static List<VirtualFolderInfo> SelectableLibraries(ILibraryManager library)
    {
        try
        {
            return (library.GetVirtualFolders() ?? new List<VirtualFolderInfo>())
                .Where(v => v != null && !string.IsNullOrEmpty(v.ItemId))
                .Where(v => v.CollectionType == null || !IgnoredCollectionTypes.Contains(v.CollectionType.ToString()!.ToLowerInvariant()))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return new List<VirtualFolderInfo>();
        }
    }

    /// <summary>
    /// Path as compared: forward slashes, no trailing separator. Empty for
    /// blanks and for a bare root ("/", "C:\"), which would leave out everything.
    /// </summary>
    public static string NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var p = raw.Trim().Replace('\\', '/');
        // Collapse repeated separators, keeping a leading "//" (UNC share).
        var unc = p.StartsWith("//", StringComparison.Ordinal);
        p = System.Text.RegularExpressions.Regex.Replace(p, "/{2,}", "/");
        if (unc) p = "/" + p;
        p = p.TrimEnd('/');
        if (p.Length == 0) return string.Empty;
        if (p.Length == 2 && p[1] == ':') return string.Empty;   // "C:"
        return p;
    }

    /// <summary>True when <paramref name="path"/> is the folder <paramref name="prefix"/> or inside it.</summary>
    public static bool IsUnder(string? path, string prefix)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefix)) return false;
        var p = path.Replace('\\', '/');
        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        // Folder boundary: "/media/anime" must not cover "/media/anime2".
        return p.Length == prefix.Length || p[prefix.Length] == '/';
    }

    /// <summary>An item is excluded when any of its libraries is, or its path is under an excluded one.</summary>
    public bool IsExcluded(BaseItem? item)
    {
        if (IsEmpty || item == null) return false;
        try
        {
            if (_paths.Count > 0)
            {
                // Movies and episodes: their file (or folder, for disc rips).
                // Series: their folder, which holds the episodes too.
                var path = item.Path;
                foreach (var prefix in _paths)
                {
                    if (IsUnder(path, prefix)) return true;
                }
            }

            if (_excluded.Count > 0)
            {
                foreach (var f in _library.GetCollectionFolders(item))
                {
                    if (f != null && _excluded.Contains(f.Id)) return true;
                }
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public List<BaseItem> Apply(IEnumerable<BaseItem> items)
        => IsEmpty ? items.ToList() : items.Where(i => !IsExcluded(i)).ToList();
}
