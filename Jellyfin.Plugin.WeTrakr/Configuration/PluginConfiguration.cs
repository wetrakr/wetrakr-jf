using System;
using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WeTrakr.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        ApiBaseUrl = "https://api.wetrakr.com";
        ScrobblePlaying = true;
        ScrobbleWatched = true;
        ScrobbleRatings = true;
    }

    /// <summary>
    /// Base URL of the WeTrakr API. Default: https://api.wetrakr.com. Advanced users
    /// who self-host WeTrakr can override this.
    /// </summary>
    public string ApiBaseUrl { get; set; }

    /// <summary>Send PlaybackStart / Progress / Pause / Unpause / Stop events.</summary>
    public bool ScrobblePlaying { get; set; }

    /// <summary>Send ItemMarkedPlayed events (reserved for plugin v2).</summary>
    public bool ScrobbleWatched { get; set; }

    /// <summary>Send UserDataSaved (ratings/favorites) events (reserved for plugin v3).</summary>
    public bool ScrobbleRatings { get; set; }

    /// <summary>Per-user WeTrakr connections. Each user pairs their own account.</summary>
    public Collection<UserConnection> UserConnections { get; set; } = new();

    /// <summary>Find a user's connection by Jellyfin user id. Null if not connected.</summary>
    public UserConnection? FindByUser(Guid userId)
    {
        var key = userId.ToString("N");
        foreach (var c in UserConnections)
        {
            if (string.Equals(c.UserId, key, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return null;
    }
}

/// <summary>One Jellyfin user's WeTrakr pairing. Public parameterless ctor => XML-serializable.</summary>
public class UserConnection
{
    public string UserId { get; set; } = string.Empty;        // Guid "N" (dashless)
    public string WebhookToken { get; set; } = string.Empty;  // = OAuth access_token
    public string Username { get; set; } = string.Empty;      // WeTrakr display name
    public DateTime? LastScrobbleAt { get; set; }
    public long ScrobbleCount { get; set; }
}
