using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.WeTrakr.Api;
using Jellyfin.Plugin.WeTrakr.Configuration;
using Jellyfin.Plugin.WeTrakr.Scrobbling;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WeTrakr.Controllers;

/// <summary>
/// Self-service endpoints for any authenticated Jellyfin user under /Plugins/WeTrakr.
/// The caller identity always comes from the token (IAuthorizationContext),
/// never from the request body — a user can only ever pair themselves.
/// </summary>
[ApiController]
[Route("Plugins/WeTrakr")]
[Authorize]
public class WeTrakrUserController : ControllerBase
{
    private readonly DeviceCodeClient _device;
    private readonly PendingPairingStore _store;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<WeTrakrUserController> _logger;

    public WeTrakrUserController(
        DeviceCodeClient device,
        PendingPairingStore store,
        IAuthorizationContext authContext,
        ILogger<WeTrakrUserController> logger)
    {
        _device = device;
        _store = store;
        _authContext = authContext;
        _logger = logger;
    }

    /// <summary>Self-contained connect page. Served to any browser; data calls below are token-gated.</summary>
    [AllowAnonymous]
    [HttpGet("Connect")]
    public ContentResult Connect()
        => Content(Plugin.ReadEmbedded("connectPage.html"), "text/html");

    [HttpPost("ConnectStart")]
    public async Task<ActionResult<DeviceCodeResponse>> ConnectStart(CancellationToken ct)
    {
        var userId = await CallerId();
        if (userId == Guid.Empty) return Unauthorized();

        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        var code = await _device.RequestCodeAsync(cfg.ApiBaseUrl, ct);
        if (code == null) return StatusCode(StatusCodes.Status502BadGateway, new { error = "device_code_request_failed" });

        _store.Set(userId, code.DeviceCode, DateTime.UtcNow.AddSeconds(code.ExpiresIn > 0 ? code.ExpiresIn : 600));
        _logger.LogInformation("[WeTrakr] Pairing started for user {UserId}", userId.ToString("N"));
        return code;
    }

    [HttpPost("Poll")]
    public async Task<ActionResult<PollStatus>> Poll(CancellationToken ct)
    {
        var userId = await CallerId();
        if (userId == Guid.Empty) return Unauthorized();

        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        var deviceCode = _store.Get(userId);
        if (string.IsNullOrEmpty(deviceCode)) return Ok(new PollStatus { Status = "no_pending_code" });

        var result = await _device.PollTokenAsync(cfg.ApiBaseUrl, deviceCode, ct);
        if (!string.IsNullOrEmpty(result.AccessToken))
        {
            var name = result.Username ?? string.Empty;
            lock (Plugin.ConfigLock)
            {
                var conn = cfg.FindByUser(userId);
                if (conn == null)
                {
                    conn = new UserConnection { UserId = userId.ToString("N") };
                    cfg.UserConnections.Add(conn);
                }

                conn.WebhookToken = result.AccessToken!;
                conn.Username = name;
                Plugin.Instance!.SaveConfiguration();
            }

            _store.Clear(userId);
            return Ok(new PollStatus { Status = "connected", Username = name });
        }

        return Ok(new PollStatus { Status = result.Error ?? "unknown" });
    }

    [HttpPost("Disconnect")]
    public async Task<ActionResult> Disconnect()
    {
        var userId = await CallerId();
        if (userId == Guid.Empty) return Unauthorized();

        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        lock (Plugin.ConfigLock)
        {
            var conn = cfg.FindByUser(userId);
            if (conn != null)
            {
                cfg.UserConnections.Remove(conn);
                Plugin.Instance!.SaveConfiguration();
            }
        }

        _store.Clear(userId);
        return NoContent();
    }

    [HttpGet("Status")]
    public async Task<ActionResult<UserStatus>> Status()
    {
        var userId = await CallerId();
        if (userId == Guid.Empty) return Unauthorized();

        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        UserConnection? conn;
        lock (Plugin.ConfigLock)
        {
            conn = cfg.FindByUser(userId);
        }

        return Ok(new UserStatus
        {
            Connected = conn != null,
            Username = conn?.Username ?? string.Empty,
            LastScrobbleAt = conn?.LastScrobbleAt,
            ScrobbleCount = conn?.ScrobbleCount ?? 0
        });
    }

    private async Task<Guid> CallerId()
    {
        var info = await _authContext.GetAuthorizationInfo(Request);
        return info.UserId;
    }
}

public class PollStatus
{
    public string Status { get; set; } = string.Empty;
    public string? Username { get; set; }
}

public class UserStatus
{
    public bool Connected { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime? LastScrobbleAt { get; set; }
    public long ScrobbleCount { get; set; }
}
