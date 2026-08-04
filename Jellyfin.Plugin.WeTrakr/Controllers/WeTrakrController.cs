using System.Linq;
using Jellyfin.Plugin.WeTrakr.Api;
using Jellyfin.Plugin.WeTrakr.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WeTrakr.Controllers;

/// <summary>
/// HTTP endpoints exposed by the plugin under /Plugins/WeTrakr.
/// Consumed exclusively from configPage.js in the plugin settings page.
/// All endpoints require Jellyfin admin rights.
///
/// Pairing is per Jellyfin account: the admin picks an account in the dropdown
/// and that account's owner confirms the short code on wetrakr.com/activate with
/// their OWN WeTrakr session, so the admin never gains access to their profile.
/// </summary>
[ApiController]
[Route("Plugins/WeTrakr")]
[Authorize(Policy = "RequiresElevation")]
public class WeTrakrController : ControllerBase
{
    private readonly DeviceCodeClient _device;

    // In-memory, single-active-pairing state: one admin drives the config page
    // at a time and the device code is short-lived (10 min). If the page is
    // reloaded mid-flow the pending code is lost — acceptable UX.
    private static string? _pendingDeviceCode;
    private static string? _pendingJellyfinUserId;
    private static string? _pendingJellyfinUserName;

    public WeTrakrController(DeviceCodeClient device)
    {
        _device = device;
    }

    /// <summary>
    /// Starts pairing for one Jellyfin account: requests a user_code from WeTrakr.
    /// </summary>
    [HttpPost("ConnectStart")]
    public async Task<ActionResult<DeviceCodeResponse>> ConnectStart([FromBody] ConnectStartDto? dto, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        var userId = NormalizeId(dto?.UserId);
        if (string.IsNullOrEmpty(userId))
        {
            return BadRequest(new { error = "missing_user_id" });
        }

        var code = await _device.RequestCodeAsync(cfg.ApiBaseUrl, ct);
        if (code == null) return StatusCode(StatusCodes.Status502BadGateway, new { error = "device_code_request_failed" });

        _pendingDeviceCode = code.DeviceCode;
        _pendingJellyfinUserId = userId;
        _pendingJellyfinUserName = dto?.UserName ?? string.Empty;
        return code;
    }

    /// <summary>
    /// Polls WeTrakr for the token of the pending pairing. On success stores the
    /// link between that Jellyfin account and the WeTrakr account that confirmed
    /// the code. Returns a status object the JS page uses to drive its state machine.
    /// </summary>
    [HttpPost("Poll")]
    public async Task<ActionResult<PollStatus>> Poll(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        if (string.IsNullOrEmpty(_pendingDeviceCode) || string.IsNullOrEmpty(_pendingJellyfinUserId))
        {
            return Ok(new PollStatus { Status = "no_pending_code" });
        }

        var result = await _device.PollTokenAsync(cfg.ApiBaseUrl, _pendingDeviceCode, ct);

        if (!string.IsNullOrEmpty(result.AccessToken))
        {
            var userId = _pendingJellyfinUserId!;
            var userName = _pendingJellyfinUserName ?? string.Empty;
            var weTrakrUser = result.Username ?? string.Empty;

            if (cfg.UserLinks == null) cfg.UserLinks = new List<UserLink>();

            // A WeTrakr account holds a single Jellyfin webhook token, and each
            // activation replaces it. If this WeTrakr account was already linked
            // to another Jellyfin account, that older link now points at a dead
            // token — drop it instead of leaving it silently broken.
            var stale = cfg.UserLinks
                .Where(l => !string.Equals(l.JellyfinUserId, userId, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(weTrakrUser)
                            && string.Equals(l.WeTrakrUsername, weTrakrUser, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var link in stale) cfg.UserLinks.Remove(link);

            var existing = cfg.FindLink(userId);
            if (existing == null)
            {
                existing = new UserLink { JellyfinUserId = userId };
                cfg.UserLinks.Add(existing);
            }

            existing.JellyfinUserName = userName;
            existing.WebhookToken = result.AccessToken!;
            existing.WeTrakrUsername = weTrakrUser;
            existing.LinkedAt = DateTime.UtcNow;
            existing.LastScrobbleAt = null;
            existing.ScrobbleCount = 0;

            // The legacy single-token pairing is superseded the moment any
            // account is linked the new way: keeping it would scrobble every
            // other account into that same WeTrakr profile.
            cfg.WebhookToken = string.Empty;
            cfg.Username = string.Empty;
            cfg.OwnerUserId = string.Empty;

            Plugin.Instance!.SaveConfiguration();
            ClearPending();
            return Ok(new PollStatus
            {
                Status = "connected",
                Username = weTrakrUser,
                UserId = userId,
                StaleLinksRemoved = stale.Count
            });
        }

        // Error codes from the backend: authorization_pending, expired_token, ...
        return Ok(new PollStatus { Status = result.Error ?? "unknown" });
    }

    /// <summary>Drops the pending pairing without touching stored links.</summary>
    [HttpPost("CancelPairing")]
    public ActionResult CancelPairing()
    {
        ClearPending();
        return NoContent();
    }

    /// <summary>
    /// Unlinks one Jellyfin account (body.UserId). With no body, clears the
    /// legacy single-token pairing. The WeTrakr-side connection is kept intact.
    /// </summary>
    [HttpPost("Disconnect")]
    public ActionResult Disconnect([FromBody] ConnectStartDto? dto)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        var userId = NormalizeId(dto?.UserId);
        if (!string.IsNullOrEmpty(userId))
        {
            var link = cfg.FindLink(userId);
            if (link != null) cfg.UserLinks.Remove(link);

            // Disconnecting the account behind the legacy pairing must also drop
            // the global token, or its events would keep flowing.
            if (string.Equals(cfg.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                ClearLegacy(cfg);
            }
        }
        else
        {
            ClearLegacy(cfg);
        }

        Plugin.Instance!.SaveConfiguration();
        ClearPending();
        return NoContent();
    }

    /// <summary>Returns the current links + settings snapshot.</summary>
    [HttpGet("Status")]
    public ActionResult<StatusSnapshot> Status()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        var links = (cfg.UserLinks ?? new List<UserLink>())
            .Where(l => !string.IsNullOrEmpty(l.WebhookToken))
            .Select(l => new UserLinkDto
            {
                UserId = l.JellyfinUserId,
                UserName = l.JellyfinUserName,
                Username = l.WeTrakrUsername,
                LastScrobbleAt = l.LastScrobbleAt,
                ScrobbleCount = l.ScrobbleCount
            })
            .ToList();

        return Ok(new StatusSnapshot
        {
            Links = links,
            LegacyMode = cfg.IsLegacyMode(),
            LegacyUsername = cfg.Username,
            LegacyOwnerUserId = cfg.OwnerUserId,
            ApiBaseUrl = cfg.ApiBaseUrl,
            ScrobblePlaying = cfg.ScrobblePlaying,
            ScrobbleWatched = cfg.ScrobbleWatched,
            ScrobbleRatings = cfg.ScrobbleRatings,
            LastScrobbleAt = cfg.LastScrobbleAt,
            ScrobbleCount = cfg.ScrobbleCount
        });
    }

    /// <summary>Updates a single boolean setting. Other fields ignored.</summary>
    [HttpPost("Settings")]
    public ActionResult UpdateSettings([FromBody] SettingsUpdateDto dto)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        if (dto.ScrobblePlaying.HasValue) cfg.ScrobblePlaying = dto.ScrobblePlaying.Value;
        if (dto.ScrobbleWatched.HasValue) cfg.ScrobbleWatched = dto.ScrobbleWatched.Value;
        if (dto.ScrobbleRatings.HasValue) cfg.ScrobbleRatings = dto.ScrobbleRatings.Value;

        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    private static void ClearLegacy(PluginConfiguration cfg)
    {
        cfg.WebhookToken = string.Empty;
        cfg.Username = string.Empty;
        cfg.OwnerUserId = string.Empty;
        cfg.LastScrobbleAt = null;
        cfg.ScrobbleCount = 0;
    }

    private static void ClearPending()
    {
        _pendingDeviceCode = null;
        _pendingJellyfinUserId = null;
        _pendingJellyfinUserName = null;
    }

    /// <summary>Jellyfin ids travel as dashed GUIDs in the web client; store them as "N".</summary>
    private static string NormalizeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return Guid.TryParse(raw, out var id) ? id.ToString("N") : string.Empty;
    }
}

public class ConnectStartDto
{
    public string? UserId { get; set; }
    public string? UserName { get; set; }
}

public class PollStatus
{
    public string Status { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? UserId { get; set; }
    public int StaleLinksRemoved { get; set; }
}

public class UserLinkDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime? LastScrobbleAt { get; set; }
    public long ScrobbleCount { get; set; }
}

public class StatusSnapshot
{
    public List<UserLinkDto> Links { get; set; } = new();
    public bool LegacyMode { get; set; }
    public string LegacyUsername { get; set; } = string.Empty;
    public string LegacyOwnerUserId { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public bool ScrobblePlaying { get; set; }
    public bool ScrobbleWatched { get; set; }
    public bool ScrobbleRatings { get; set; }
    public DateTime? LastScrobbleAt { get; set; }
    public long ScrobbleCount { get; set; }
}

public class SettingsUpdateDto
{
    public bool? ScrobblePlaying { get; set; }
    public bool? ScrobbleWatched { get; set; }
    public bool? ScrobbleRatings { get; set; }
}
