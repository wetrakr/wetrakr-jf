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
/// </summary>
[ApiController]
[Route("Plugins/WeTrakr")]
[Authorize(Policy = "RequiresElevation")]
public class WeTrakrController : ControllerBase
{
    private readonly DeviceCodeClient _device;

    // In-memory, single-active-pairing state. The plugin only serves one admin,
    // and the device code is short-lived (10 min). If admin reloads the config
    // page mid-flow, they lose the pending code — that's acceptable UX.
    private static string? _pendingDeviceCode;

    public WeTrakrController(DeviceCodeClient device)
    {
        _device = device;
    }

    /// <summary>Starts pairing: requests a user_code from WeTrakr.</summary>
    [HttpPost("ConnectStart")]
    public async Task<ActionResult<DeviceCodeResponse>> ConnectStart(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        var code = await _device.RequestCodeAsync(cfg.ApiBaseUrl, ct);
        if (code == null) return StatusCode(StatusCodes.Status502BadGateway, new { error = "device_code_request_failed" });

        _pendingDeviceCode = code.DeviceCode;
        return code;
    }

    /// <summary>
    /// Polls WeTrakr for the token. Persists it to plugin configuration on success.
    /// Returns a status object the JS page uses to drive the state machine.
    /// </summary>
    [HttpPost("Poll")]
    public async Task<ActionResult<PollStatus>> Poll(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        if (string.IsNullOrEmpty(_pendingDeviceCode))
        {
            return Ok(new PollStatus { Status = "no_pending_code" });
        }

        var result = await _device.PollTokenAsync(cfg.ApiBaseUrl, _pendingDeviceCode, ct);

        if (!string.IsNullOrEmpty(result.AccessToken))
        {
            cfg.WebhookToken = result.AccessToken!;
            cfg.Username = result.Username ?? string.Empty;
            Plugin.Instance!.SaveConfiguration();
            _pendingDeviceCode = null;
            return Ok(new PollStatus { Status = "connected", Username = cfg.Username });
        }

        // Error codes from the backend: authorization_pending, expired_token, ...
        return Ok(new PollStatus { Status = result.Error ?? "unknown" });
    }

    /// <summary>Clears the stored webhook token. The API-side state is kept intact.</summary>
    [HttpPost("Disconnect")]
    public ActionResult Disconnect()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        cfg.WebhookToken = string.Empty;
        cfg.Username = string.Empty;
        cfg.LastScrobbleAt = null;
        cfg.ScrobbleCount = 0;
        Plugin.Instance!.SaveConfiguration();
        _pendingDeviceCode = null;
        return NoContent();
    }

    /// <summary>Returns current connection + settings snapshot.</summary>
    [HttpGet("Status")]
    public ActionResult<StatusSnapshot> Status()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        return Ok(new StatusSnapshot
        {
            Connected = !string.IsNullOrEmpty(cfg.WebhookToken),
            Username = cfg.Username,
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
}

public class PollStatus
{
    public string Status { get; set; } = string.Empty;
    public string? Username { get; set; }
}

public class StatusSnapshot
{
    public bool Connected { get; set; }
    public string Username { get; set; } = string.Empty;
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
