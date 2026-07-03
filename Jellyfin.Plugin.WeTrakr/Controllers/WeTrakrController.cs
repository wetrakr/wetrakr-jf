using System;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.WeTrakr.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WeTrakr.Controllers;

/// <summary>
/// Admin-only overview + global settings under /Plugins/WeTrakr/Admin.
/// Consumed from configPage.html in the plugin settings page.
/// The Admin/ prefix keeps these routes disjoint from the per-user controller.
/// </summary>
[ApiController]
[Route("Plugins/WeTrakr/Admin")]
[Authorize(Policy = "RequiresElevation")]
public class WeTrakrController : ControllerBase
{
    private readonly IUserManager _userManager;

    public WeTrakrController(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <summary>Global settings snapshot + all connected users with resolved names.</summary>
    [HttpGet("Status")]
    public ActionResult<AdminStatus> Status()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        var users = new Collection<AdminConnection>();
        lock (Plugin.ConfigLock)
        {
            foreach (var c in cfg.UserConnections)
            {
                users.Add(new AdminConnection
                {
                    UserId = c.UserId,
                    JellyfinName = ResolveName(c.UserId) ?? string.Empty,
                    WeTrakrName = c.Username,
                    LastScrobbleAt = c.LastScrobbleAt,
                    ScrobbleCount = c.ScrobbleCount
                });
            }
        }

        return Ok(new AdminStatus
        {
            ApiBaseUrl = cfg.ApiBaseUrl,
            ScrobblePlaying = cfg.ScrobblePlaying,
            ScrobbleWatched = cfg.ScrobbleWatched,
            ScrobbleRatings = cfg.ScrobbleRatings,
            Users = users
        });
    }

    /// <summary>Updates global settings. Only supplied fields change.</summary>
    [HttpPost("Settings")]
    public ActionResult UpdateSettings([FromBody] SettingsUpdateDto dto)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(StatusCodes.Status500InternalServerError);

        if (dto.ScrobblePlaying.HasValue) cfg.ScrobblePlaying = dto.ScrobblePlaying.Value;
        if (dto.ScrobbleWatched.HasValue) cfg.ScrobbleWatched = dto.ScrobbleWatched.Value;
        if (dto.ScrobbleRatings.HasValue) cfg.ScrobbleRatings = dto.ScrobbleRatings.Value;
        if (!string.IsNullOrWhiteSpace(dto.ApiBaseUrl)) cfg.ApiBaseUrl = dto.ApiBaseUrl.Trim();

        lock (Plugin.ConfigLock)
        {
            Plugin.Instance!.SaveConfiguration();
        }

        return NoContent();
    }

    // User entity type moved namespaces between Jellyfin versions; reflection keeps
    // name resolution ABI-stable across the net8/net9 SDK targets.
    private string? ResolveName(string dashlessId)
    {
        try
        {
            if (!Guid.TryParse(dashlessId, out var id)) return null;

            // GetUserById's return type differs across 10.10/10.11, so a direct call
            // throws MissingMethodException at JIT (uncatchable here). Invoke reflectively.
            var method = _userManager.GetType().GetMethod("GetUserById", new[] { typeof(Guid) });
            var user = method?.Invoke(_userManager, new object[] { id });
            if (user == null) return null;

            var prop = user.GetType().GetProperty("Username") ?? user.GetType().GetProperty("Name");
            return prop?.GetValue(user) as string;
        }
        catch
        {
            return null;
        }
    }
}

public class AdminStatus
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public bool ScrobblePlaying { get; set; }
    public bool ScrobbleWatched { get; set; }
    public bool ScrobbleRatings { get; set; }
    public Collection<AdminConnection> Users { get; set; } = new();
}

public class AdminConnection
{
    public string UserId { get; set; } = string.Empty;
    public string JellyfinName { get; set; } = string.Empty;
    public string WeTrakrName { get; set; } = string.Empty;
    public DateTime? LastScrobbleAt { get; set; }
    public long ScrobbleCount { get; set; }
}

public class SettingsUpdateDto
{
    public bool? ScrobblePlaying { get; set; }
    public bool? ScrobbleWatched { get; set; }
    public bool? ScrobbleRatings { get; set; }
    public string? ApiBaseUrl { get; set; }
}
