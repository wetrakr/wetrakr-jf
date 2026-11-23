using System.Net.Http.Json;
using Jellyfin.Plugin.WeTrakr.Configuration;
using Jellyfin.Plugin.WeTrakr.Scrobbling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WeTrakr.Api;

/// <summary>
/// POSTs scrobble payloads to {ApiBaseUrl}/webhooks/jellyfin/{WebhookToken}.
/// One retry on a network error or a 5xx — scrobble must never throw into the
/// event loop or Jellyfin playback pipeline. Returns whether the event got
/// through, so callers only record what WeTrakr actually received (what did
/// not arrive is picked up by the library sync).
/// </summary>
public class WeTrakrClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<WeTrakrClient> _logger;

    public WeTrakrClient(IHttpClientFactory factory, ILogger<WeTrakrClient> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<bool> SendAsync(PluginConfiguration config, ScrobbleTarget target, ScrobblePayload payload, CancellationToken ct)
    {
        if (target == null || string.IsNullOrEmpty(target.WebhookToken) || string.IsNullOrEmpty(config.ApiBaseUrl))
        {
            return false;
        }

        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/webhooks/jellyfin/{target.WebhookToken}";
        var http = _factory.CreateClient(HttpClientNames.WeTrakr);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent.Value);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var response = await http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    if (status >= 500 && attempt == 1)
                    {
                        _logger.LogDebug("[WeTrakr] WeTrakr answered HTTP {Status} to {Event} for {Item}, retrying", status, payload.Event, ScrobbleLog.Describe(payload));
                        continue;
                    }

                    var detail = await ReadDetailAsync(response, ct).ConfigureAwait(false);
                    _logger.LogWarning(
                        "[WeTrakr] WeTrakr rejected {Event} for {Item} ({Ids}): HTTP {Status}{Detail}",
                        payload.Event, ScrobbleLog.Describe(payload), ScrobbleLog.Ids(payload), status, detail);
                    return false;
                }

                // Update local bookkeeping — best-effort, non-critical.
                // Per-account counters when the event went through a link, the
                // global ones while the legacy single-token pairing is in use.
                if (Plugin.Instance != null)
                {
                    if (target.Link != null)
                    {
                        target.Link.LastScrobbleAt = DateTime.UtcNow;
                        target.Link.ScrobbleCount++;
                    }
                    else
                    {
                        Plugin.Instance.Configuration.LastScrobbleAt = DateTime.UtcNow;
                        Plugin.Instance.Configuration.ScrobbleCount++;
                    }
                    Plugin.Instance.SaveConfiguration();
                }

                LogSent(target, payload);
                return true;
            }
            catch (HttpRequestException ex) when (attempt == 1)
            {
                _logger.LogDebug(ex, "[WeTrakr] POST attempt 1 failed for event {Event}, retrying", payload.Event);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WeTrakr] Could not reach WeTrakr for {Event} of {Item}; the library sync will pick it up", payload.Event, ScrobbleLog.Describe(payload));
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// One line per event that reached WeTrakr, with the ids it was sent with:
    /// when a title lands on the wrong entry (or nowhere), these ids are what
    /// to check in Jellyfin's metadata. Progress ticks only at Debug.
    /// </summary>
    private void LogSent(ScrobbleTarget target, ScrobblePayload payload)
    {
        var level = payload.Event == "PlaybackProgress" ? LogLevel.Debug : LogLevel.Information;
        if (!_logger.IsEnabled(level)) return;

        var account = string.IsNullOrEmpty(target.Link?.WeTrakrUsername) ? "WeTrakr" : target.Link!.WeTrakrUsername;
        _logger.Log(
            level,
            "[WeTrakr] Sent {Event} for {Item} ({Ids}) from Jellyfin user {User} to {Account}",
            payload.Event, ScrobbleLog.Describe(payload), ScrobbleLog.Ids(payload),
            string.IsNullOrEmpty(payload.UserName) ? payload.UserId : payload.UserName, account);
    }

    /// <summary>The start of the error body WeTrakr sent back, if any.</summary>
    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (body.Length == 0) return string.Empty;
            if (body.Length > 300) body = body[..300] + "...";
            return " " + body.ReplaceLineEndings(" ");
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal static class UserAgent
{
    public static readonly string PluginVersion = typeof(UserAgent).Assembly.GetName().Version?.ToString() ?? "0";

    public static readonly string Value = $"WeTrakr-Jellyfin/{PluginVersion}";
}
