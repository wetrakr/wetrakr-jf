using System.Collections.Concurrent;

namespace Jellyfin.Plugin.WeTrakr.Sync;

/// <summary>
/// Items whose watch already reached WeTrakr through the live scrobble. The
/// sync's diff does not upload them again as a new play (the API dedupes plays
/// within 24 h, but a watch that crosses that window would count twice).
/// </summary>
public static class LiveSentRegistry
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private static readonly ConcurrentDictionary<string, DateTime> Sent = new();

    private static string Key(string userId, Guid itemId) => userId + ":" + itemId.ToString("N");

    public static void MarkPlayed(string userId, Guid itemId)
    {
        Sent[Key(userId, itemId)] = DateTime.UtcNow;
        if (Sent.Count > 20000)
        {
            var cutoff = DateTime.UtcNow - Window;
            foreach (var k in Sent.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) Sent.TryRemove(k, out _);
        }
    }

    /// <summary>Was a watched mark for this item sent live after `since`?</summary>
    public static bool PlayedSince(string userId, Guid itemId, DateTime since)
        => Sent.TryGetValue(Key(userId, itemId), out var at) && at >= since && DateTime.UtcNow - at < Window;
}
