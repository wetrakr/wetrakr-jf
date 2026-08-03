using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WeTrakr.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        ApiBaseUrl = "https://api.wetrakr.com";
        WebhookToken = string.Empty;
        Username = string.Empty;
        OwnerUserId = string.Empty;
        ScrobblePlaying = true;
        ScrobbleWatched = true;
        ScrobbleRatings = true;
        LastScrobbleAt = null;
        ScrobbleCount = 0;
    }

    /// <summary>
    /// Base URL of the WeTrakr API. Default: https://api.wetrakr.com. Advanced users
    /// who self-host WeTrakr can override this.
    /// </summary>
    public string ApiBaseUrl { get; set; }

    /// <summary>
    /// Token issued by the WeTrakr device-code flow. Used as the path segment when
    /// POSTing to /webhooks/jellyfin/{WebhookToken}.
    /// Empty when the plugin is not yet connected.
    /// </summary>
    public string WebhookToken { get; set; }

    /// <summary>
    /// Display name of the WeTrakr user the plugin is paired with. Shown in the
    /// config page "Connected as" label. Not used in auth.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Jellyfin user id (GUID "N" format) of the account that paired the plugin
    /// with WeTrakr. Events coming from this account are flagged is_owner=true so
    /// the API can bind the connection to the right person on a shared server
    /// instead of guessing from whoever plays first. Empty when the pairing
    /// predates this field or the id could not be read from the request.
    /// </summary>
    public string OwnerUserId { get; set; }

    /// <summary>Send PlaybackStart / Progress / Pause / Unpause / Stop events.</summary>
    public bool ScrobblePlaying { get; set; }

    /// <summary>Send ItemMarkedPlayed events (reserved for plugin v2).</summary>
    public bool ScrobbleWatched { get; set; }

    /// <summary>Send UserDataSaved (ratings/favorites) events (reserved for plugin v3).</summary>
    public bool ScrobbleRatings { get; set; }

    public DateTime? LastScrobbleAt { get; set; }

    public long ScrobbleCount { get; set; }
}
