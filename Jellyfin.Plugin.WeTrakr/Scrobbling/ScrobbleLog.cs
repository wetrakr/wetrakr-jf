using System.Collections.Concurrent;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Helpers that make the scrobble log readable without Debug logging: a short
/// description of the item and the ids sent (what WeTrakr matches on), and a
/// gate so a reason that repeats on every pause or tick is written once.
/// </summary>
internal static class ScrobbleLog
{
    /// <summary>How long a repeated skip stays quiet after its first line.</summary>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(6);

    private const int MaxKeys = 2000;

    private static readonly ConcurrentDictionary<string, DateTime> LastLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// True the first time a key is seen in RepeatWindow: the caller logs at
    /// Information then, and at Debug the rest of the time.
    /// </summary>
    public static bool FirstInWindow(string key, DateTime now)
    {
        if (LastLogged.Count > MaxKeys)
        {
            LastLogged.Clear();
        }

        if (LastLogged.TryGetValue(key, out var last) && now - last < RepeatWindow)
        {
            return false;
        }

        LastLogged[key] = now;
        return true;
    }

    /// <summary>"Show S01E02 "Episode"" or ""Movie"".</summary>
    public static string Describe(ScrobblePayload p)
    {
        if (p.ItemType == "Episode")
        {
            var series = string.IsNullOrEmpty(p.SeriesName) ? "?" : p.SeriesName;
            return $"{series} S{p.Season:D2}E{p.Episode:D2} \"{p.Name}\"";
        }

        return $"\"{p.Name}\"";
    }

    /// <summary>The ids WeTrakr matches on, or a hint when there are none.</summary>
    public static string Ids(ScrobblePayload p)
    {
        var parts = new List<string>();
        AddIds(parts, string.Empty, p.ProviderIds);
        if (p.ItemType == "Episode")
        {
            AddIds(parts, "series ", p.SeriesProviderIds);
        }

        return parts.Count == 0
            ? "no TMDB/TVDB/IMDb ids in Jellyfin, so WeTrakr can only match by title"
            : string.Join(", ", parts);
    }

    private static void AddIds(List<string> parts, string prefix, ProviderIdsPayload? ids)
    {
        if (ids == null) return;
        if (!string.IsNullOrEmpty(ids.Tmdb)) parts.Add($"{prefix}tmdb {ids.Tmdb}");
        if (!string.IsNullOrEmpty(ids.Tvdb)) parts.Add($"{prefix}tvdb {ids.Tvdb}");
        if (!string.IsNullOrEmpty(ids.Imdb)) parts.Add($"{prefix}imdb {ids.Imdb}");
    }
}
