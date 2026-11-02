using Jellyfin.Plugin.WeTrakr.Api;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>External ids (TMDB / IMDb / TVDB) of a Jellyfin item.</summary>
public static class ItemIds
{
    public static string? Get(BaseItem? item, string provider)
    {
        if (item?.ProviderIds == null) return null;
        foreach (var kv in item.ProviderIds)
        {
            if (string.Equals(kv.Key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                return kv.Value.Trim();
            }
        }
        return null;
    }

    public static ExtIds Ext(BaseItem? item)
    {
        var ids = new ExtIds();
        if (item == null) return ids;
        if (long.TryParse(Get(item, "Tmdb"), out var tmdb) && tmdb > 0) ids.Tmdb = tmdb;
        var imdb = Get(item, "Imdb");
        if (!string.IsNullOrEmpty(imdb) && imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) ids.Imdb = imdb.ToLowerInvariant();
        if (long.TryParse(Get(item, "Tvdb"), out var tvdb) && tvdb > 0) ids.Tvdb = tvdb;
        return ids;
    }

    /// <summary>Item id as stored in the sync state: GUID "N".</summary>
    public static string Key(BaseItem item) => item.Id.ToString("N");
}
