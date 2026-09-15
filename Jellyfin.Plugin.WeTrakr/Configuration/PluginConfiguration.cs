using System;
using System.Collections.Generic;
using System.Linq;
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
        UserLinks = new List<UserLink>();
        ScrobblePlaying = true;
        ScrobbleWatched = true;
        ScrobbleRatings = true;
        LastScrobbleAt = null;
        ScrobbleCount = 0;
        ExcludedLibraries = new List<string>();
    }

    /// <summary>
    /// Base URL of the WeTrakr API. Default: https://api.wetrakr.com. Advanced users
    /// who self-host WeTrakr can override this.
    /// </summary>
    public string ApiBaseUrl { get; set; }

    /// <summary>
    /// One entry per Jellyfin account that has been paired with a WeTrakr account.
    /// A shared server has several: each account scrobbles to its own WeTrakr
    /// profile, using its own webhook token. Accounts with no entry here are not
    /// scrobbled at all — the filtering happens on this side, before the request.
    /// </summary>
    public List<UserLink> UserLinks { get; set; }

    /// <summary>
    /// LEGACY (pre-multi-account) token: applied to every Jellyfin account when
    /// <see cref="UserLinks"/> is empty, so servers that paired with an older
    /// build keep scrobbling after the update. The API-side allowlist is what
    /// filters accounts in that mode. Cleared as soon as the admin pairs any
    /// account with the new per-user flow.
    /// </summary>
    public string WebhookToken { get; set; }

    /// <summary>
    /// Display name of the WeTrakr user of the legacy pairing. See <see cref="WebhookToken"/>.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Jellyfin user id (GUID "N" format) of the account that made the legacy
    /// pairing. Their events carry is_owner=true so the API binds the connection
    /// to the right person instead of guessing from whoever plays first. Empty
    /// when the pairing predates this field or the id could not be read.
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

    /// <summary>Libraries to exclude from scrobbling.</summary>
    public List<string> ExcludedLibraries { get; set; }
    
    /// <summary>True while the plugin is still running on the single-token legacy pairing.</summary>
    public bool IsLegacyMode()
        => (UserLinks == null || UserLinks.Count == 0) && !string.IsNullOrEmpty(WebhookToken);

    /// <summary>
    /// The link for a Jellyfin account, or null when that account has not been
    /// paired. Ids are compared as GUID "N" strings, case-insensitively.
    /// </summary>
    public UserLink? FindLink(string? jellyfinUserId)
    {
        if (string.IsNullOrEmpty(jellyfinUserId) || UserLinks == null) return null;
        return UserLinks.FirstOrDefault(l =>
            string.Equals(l.JellyfinUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where the events of a Jellyfin account must be sent, or null when that
    /// account is not paired and nothing should leave the server.
    ///
    /// Linked account  → its own token; is_owner is true because the token
    ///                   belongs to exactly that person, so the API binds the
    ///                   connection to them with no guessing involved.
    /// Legacy pairing  → the single global token for everybody; the API-side
    ///                   allowlist decides which accounts count.
    /// </summary>
    public ScrobbleTarget? ResolveTarget(string? jellyfinUserId)
    {
        var link = FindLink(jellyfinUserId);
        if (link != null && !string.IsNullOrEmpty(link.WebhookToken))
        {
            return new ScrobbleTarget { WebhookToken = link.WebhookToken, IsOwner = true, Link = link };
        }

        if (IsLegacyMode())
        {
            var isOwner = !string.IsNullOrEmpty(OwnerUserId)
                && string.Equals(OwnerUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase);
            return new ScrobbleTarget { WebhookToken = WebhookToken, IsOwner = isOwner, Link = null };
        }

        return null;
    }
}

/// <summary>Resolved destination for one event: which token, flagged how.</summary>
public class ScrobbleTarget
{
    public string WebhookToken { get; set; } = string.Empty;

    public bool IsOwner { get; set; }

    /// <summary>Link whose counters this event updates. Null in legacy mode.</summary>
    public UserLink? Link { get; set; }
}

/// <summary>
/// Pairing between one Jellyfin account and one WeTrakr account.
/// </summary>
public class UserLink
{
    /// <summary>Jellyfin user id, GUID "N" format (32 hex chars, no dashes).</summary>
    public string JellyfinUserId { get; set; } = string.Empty;

    /// <summary>Jellyfin username at pairing time. Display only — ids are what match.</summary>
    public string JellyfinUserName { get; set; } = string.Empty;

    /// <summary>Webhook token issued by WeTrakr for this account's own profile.</summary>
    public string WebhookToken { get; set; } = string.Empty;

    /// <summary>WeTrakr display name, shown in the config page.</summary>
    public string WeTrakrUsername { get; set; } = string.Empty;

    public DateTime? LinkedAt { get; set; }

    public DateTime? LastScrobbleAt { get; set; }

    public long ScrobbleCount { get; set; }
}
