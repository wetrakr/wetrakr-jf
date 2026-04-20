using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.WeTrakr.Scrobbling;

/// <summary>
/// Maps Jellyfin SessionInfo + item data into the wetrakr-api payload shape.
/// Only Movie and Episode kinds are supported — any other kind should be
/// filtered out upstream and never reach the builder.
/// </summary>
public class PayloadBuilder
{
    public ScrobblePayload Build(string eventName, BaseItem item, SessionInfo session, long positionTicks, bool isPaused, bool played)
    {
        var payload = new ScrobblePayload
        {
            Event = eventName,
            UserId = session.UserId.ToString("N"),
            UserName = session.UserName ?? string.Empty,
            ItemId = item.Id.ToString("N"),
            ItemType = item is Episode ? "Episode" : "Movie",
            Name = item.Name ?? string.Empty,
            ProviderIds = BuildProviderIds(item.ProviderIds),
            PositionTicks = positionTicks,
            RuntimeTicks = item.RunTimeTicks ?? 0,
            IsPaused = isPaused,
            Played = played
        };

        AttachEpisodeMetadata(payload, item);
        return payload;
    }

    /// <summary>
    /// Build a payload for an out-of-band UserDataSaved event (mark watched,
    /// toggle favorite, change rating). No session involved.
    /// </summary>
    public ScrobblePayload BuildUserData(string eventName, BaseItem item, UserItemData userData, User user, string saveReason)
    {
        var payload = new ScrobblePayload
        {
            Event = eventName,
            UserId = user.Id.ToString("N"),
            UserName = user.Username ?? string.Empty,
            ItemId = item.Id.ToString("N"),
            ItemType = item is Episode ? "Episode" : "Movie",
            Name = item.Name ?? string.Empty,
            ProviderIds = BuildProviderIds(item.ProviderIds),
            PositionTicks = userData.PlaybackPositionTicks,
            RuntimeTicks = item.RunTimeTicks ?? 0,
            IsPaused = false,
            Played = userData.Played,
            IsFavorite = userData.IsFavorite,
            UserRating = userData.Rating,
            SaveReason = saveReason
        };

        AttachEpisodeMetadata(payload, item);
        return payload;
    }

    private static void AttachEpisodeMetadata(ScrobblePayload payload, BaseItem item)
    {
        if (item is Episode episode)
        {
            payload.SeriesName = episode.SeriesName ?? string.Empty;
            payload.Season = episode.ParentIndexNumber ?? 0;
            payload.Episode = episode.IndexNumber ?? 0;

            var series = episode.Series;
            if (series != null)
            {
                payload.SeriesProviderIds = BuildProviderIds(series.ProviderIds);
            }
        }
    }

    private static ProviderIdsPayload BuildProviderIds(IReadOnlyDictionary<string, string>? ids)
    {
        var result = new ProviderIdsPayload();
        if (ids == null) return result;

        if (ids.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdb) && !string.IsNullOrEmpty(imdb))
            result.Imdb = imdb;
        if (ids.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdb) && !string.IsNullOrEmpty(tmdb))
            result.Tmdb = tmdb;
        if (ids.TryGetValue(MetadataProvider.Tvdb.ToString(), out var tvdb) && !string.IsNullOrEmpty(tvdb))
            result.Tvdb = tvdb;

        return result;
    }
}
