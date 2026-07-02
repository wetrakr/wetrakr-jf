using System.Net.Http.Json;
using Jellyfin.Plugin.WeTrakr.Configuration;
using Jellyfin.Plugin.WeTrakr.Scrobbling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WeTrakr.Api;

/// <summary>
/// POSTs scrobble payloads to {ApiBaseUrl}/webhooks/jellyfin/{WebhookToken}.
/// One retry on HttpRequestException — scrobble must never throw into the
/// event loop or Jellyfin playback pipeline.
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

    public async Task SendAsync(string apiBaseUrl, string webhookToken, ScrobblePayload payload, UserConnection conn, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(webhookToken) || string.IsNullOrEmpty(apiBaseUrl))
        {
            return;
        }

        var url = $"{apiBaseUrl.TrimEnd('/')}/webhooks/jellyfin/{webhookToken}";
        var http = _factory.CreateClient(HttpClientNames.WeTrakr);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent.Value);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var response = await http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // Per-user bookkeeping — best-effort. Persist only on non-progress
                // events to avoid a disk write every few seconds during playback.
                conn.LastScrobbleAt = DateTime.UtcNow;
                conn.ScrobbleCount++;
                if (!string.Equals(payload.Event, "PlaybackProgress", StringComparison.Ordinal) && Plugin.Instance != null)
                {
                    lock (Plugin.ConfigLock)
                    {
                        Plugin.Instance.SaveConfiguration();
                    }
                }
                return;
            }
            catch (HttpRequestException ex) when (attempt == 1)
            {
                _logger.LogDebug(ex, "[WeTrakr] POST attempt 1 failed for event {Event}, retrying", payload.Event);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WeTrakr] POST failed for event {Event}", payload.Event);
                return;
            }
        }
    }
}

internal static class UserAgent
{
    public static readonly string Value = $"WeTrakr-Jellyfin/{typeof(UserAgent).Assembly.GetName().Version}";
}
