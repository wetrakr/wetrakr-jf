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

    public async Task SendAsync(PluginConfiguration config, ScrobblePayload payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.WebhookToken) || string.IsNullOrEmpty(config.ApiBaseUrl))
        {
            return;
        }

        var url = $"{config.ApiBaseUrl.TrimEnd('/')}/webhooks/jellyfin/{config.WebhookToken}";
        var http = _factory.CreateClient(HttpClientNames.WeTrakr);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent.Value);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var response = await http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // Update local bookkeeping — best-effort, non-critical.
                if (Plugin.Instance != null)
                {
                    Plugin.Instance.Configuration.LastScrobbleAt = DateTime.UtcNow;
                    Plugin.Instance.Configuration.ScrobbleCount++;
                    Plugin.Instance.SaveConfiguration();
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
