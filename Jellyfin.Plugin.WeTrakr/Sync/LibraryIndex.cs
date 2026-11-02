using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WeTrakr.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>
/// Index of ONE Jellyfin account's library by external ids, to find the local
/// item of what comes from WeTrakr. Built on every pass (one query) with what
/// that account can see.
/// </summary>
public class LibraryIndex
{
    private readonly Dictionary<string, List<BaseItem>> _movies = new();
    private readonly Dictionary<string, List<BaseItem>> _series = new();
    private readonly Dictionary<string, List<BaseItem>> _episodesById = new();
    private readonly Dictionary<string, List<BaseItem>> _episodesBySe = new();
    private readonly Dictionary<Guid, List<Episode>> _episodesBySeries = new();
    private readonly Dictionary<Guid, BaseItem> _byId = new();

    public int Count => _byId.Count;

    public static LibraryIndex Build(ILibraryManager library, JfUser user)
    {
        var idx = new LibraryIndex();
        var items = LibraryFilter.Current(library).Apply(library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false
        }));

        foreach (var item in items)
        {
            idx._byId[item.Id] = item;
            if (item is Movie) AddIds(idx._movies, ItemIds.Ext(item), item);
            else if (item is Series) AddIds(idx._series, ItemIds.Ext(item), item);
        }
        foreach (var ep in items.OfType<Episode>())
        {
            AddIds(idx._episodesById, ItemIds.Ext(ep), ep);
            var seriesId = ep.SeriesId;
            if (seriesId == Guid.Empty) continue;
            if (!idx._episodesBySeries.TryGetValue(seriesId, out var list)) idx._episodesBySeries[seriesId] = list = new List<Episode>();
            list.Add(ep);
            if (ep.ParentIndexNumber != null && ep.IndexNumber != null)
            {
                Add(idx._episodesBySe, seriesId.ToString("N") + ":" + ep.ParentIndexNumber + ":" + ep.IndexNumber, ep);
            }
        }
        return idx;
    }

    private static void Add(Dictionary<string, List<BaseItem>> map, string key, BaseItem item)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<BaseItem>();
        if (!list.Contains(item)) list.Add(item);
    }

    private static void AddIds(Dictionary<string, List<BaseItem>> map, ExtIds ids, BaseItem item)
    {
        if (ids.Tmdb != null) Add(map, "tmdb:" + ids.Tmdb, item);
        if (!string.IsNullOrEmpty(ids.Imdb)) Add(map, "imdb:" + ids.Imdb, item);
        if (ids.Tvdb != null) Add(map, "tvdb:" + ids.Tvdb, item);
    }

    private static List<BaseItem> Lookup(Dictionary<string, List<BaseItem>> map, ExtIds? ids)
    {
        if (ids == null) return new List<BaseItem>();
        // TMDB first (like the API), then IMDb and TVDB.
        if (ids.Tmdb != null && map.TryGetValue("tmdb:" + ids.Tmdb, out var a)) return a;
        if (!string.IsNullOrEmpty(ids.Imdb) && map.TryGetValue("imdb:" + ids.Imdb.ToLowerInvariant(), out var b)) return b;
        if (ids.Tvdb != null && map.TryGetValue("tvdb:" + ids.Tvdb, out var c)) return c;
        return new List<BaseItem>();
    }

    public BaseItem? GetById(Guid id) => _byId.TryGetValue(id, out var it) ? it : null;

    /// <summary>Local items of a WeTrakr title (there can be several versions).</summary>
    public List<BaseItem> Find(string type, ExtIds? ids, ExtIds? seriesIds, int? season, int? episode)
    {
        switch (type)
        {
            case "movie":
                return Lookup(_movies, ids);
            case "show":
                return Lookup(_series, ids);
            case "episode":
                // Series + number first: TMDB and TVDB episode ids number anime
                // differently, and the API already resolved season/episode.
                var result = new List<BaseItem>();
                if (season != null && episode != null)
                {
                    foreach (var s in Lookup(_series, seriesIds))
                    {
                        if (_episodesBySe.TryGetValue(s.Id.ToString("N") + ":" + season + ":" + episode, out var eps)) result.AddRange(eps);
                    }
                }
                if (result.Count == 0) result.AddRange(Lookup(_episodesById, ids));
                return result;
        }
        return new List<BaseItem>();
    }

    public List<Episode> EpisodesOf(Guid seriesId)
        => _episodesBySeries.TryGetValue(seriesId, out var list) ? list : new List<Episode>();

    /// <summary>Canonical key of a WeTrakr title for the Planning set.</summary>
    public static string? TitleKey(string type, ExtIds? ids)
    {
        if (ids == null) return null;
        if (ids.Tmdb != null) return type + ":tmdb:" + ids.Tmdb;
        if (!string.IsNullOrEmpty(ids.Imdb)) return type + ":imdb:" + ids.Imdb.ToLowerInvariant();
        if (ids.Tvdb != null) return type + ":tvdb:" + ids.Tvdb;
        return null;
    }

    public static ExtIds? ParseTitleKey(string key, out string? type)
    {
        type = null;
        var parts = key?.Split(':');
        if (parts == null || parts.Length != 3) return null;
        type = parts[0];
        var ids = new ExtIds();
        if (parts[1] == "tmdb" && long.TryParse(parts[2], out var t)) ids.Tmdb = t;
        else if (parts[1] == "imdb") ids.Imdb = parts[2];
        else if (parts[1] == "tvdb" && long.TryParse(parts[2], out var v)) ids.Tvdb = v;
        return ids;
    }
}
