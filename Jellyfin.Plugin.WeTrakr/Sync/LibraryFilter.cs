using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>
/// Libraries excluded in the plugin settings: nothing in them is scrobbled or
/// synced (not uploaded, not written, not added to the playlist). Resolved
/// once per saved configuration and cached.
/// </summary>
public class LibraryFilter
{
    private static readonly string[] IgnoredCollectionTypes =
        { "music", "musicvideos", "photos", "books", "audiobooks", "playlists", "boxsets", "livetv", "homevideos" };

    private static readonly object CacheLock = new();
    private static List<string>? _cachedFor;
    private static string _cachedKey = string.Empty;
    private static LibraryFilter? _cached;

    private readonly ILibraryManager _library;
    private readonly HashSet<Guid> _excluded;

    private LibraryFilter(ILibraryManager library, HashSet<Guid> excluded)
    {
        _library = library;
        _excluded = excluded;
    }

    public bool IsEmpty => _excluded.Count == 0;

    /// <summary>Filter for the current configuration.</summary>
    public static LibraryFilter Current(ILibraryManager library)
    {
        var ids = Plugin.Instance?.Configuration?.ExcludedLibraryIds ?? new List<string>();
        var key = string.Join(",", ids);
        lock (CacheLock)
        {
            if (_cached != null && ReferenceEquals(_cachedFor, ids) && _cachedKey == key) return _cached;
        }
        var set = new HashSet<Guid>();
        foreach (var raw in ids)
        {
            if (Guid.TryParse(raw, out var g)) set.Add(g);
        }
        var filter = new LibraryFilter(library, set);
        lock (CacheLock)
        {
            _cachedFor = ids;
            _cachedKey = key;
            _cached = filter;
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

    /// <summary>An item is excluded when any of the libraries it belongs to is.</summary>
    public bool IsExcluded(BaseItem? item)
    {
        if (IsEmpty || item == null) return false;
        try
        {
            foreach (var f in _library.GetCollectionFolders(item))
            {
                if (f != null && _excluded.Contains(f.Id)) return true;
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
