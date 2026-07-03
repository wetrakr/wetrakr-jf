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

    // UtcNow.Ticks until which all sends are suppressed after a 429. Shared across
    // users because the API rate limit is per server IP, not per token.
    private long _pausedUntilTicks;

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

        // Honor an active back-off window from a previous 429.
        if (DateTime.UtcNow.Ticks < Volatile.Read(ref _pausedUntilTicks))
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

                // Rate limited — open a global back-off window so all users pause
                // together (the limit is per server, not per token) instead of hammering.
                if ((int)response.StatusCode == 429)
                {
                    var delay = GetRetryAfter(response) ?? TimeSpan.FromSeconds(30);
                    Volatile.Write(ref _pausedUntilTicks, DateTime.UtcNow.Add(delay).Ticks);
                    _logger.LogWarning("[WeTrakr] Rate limited (429); backing off {Seconds}s for all users. Headers: {Headers}",
                        (int)delay.TotalSeconds, FormatRateLimitHeaders(response));
                    return;
                }

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

    // Retry-After may be a delta (seconds) or an HTTP date; return whichever is set.
    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta;
        if (ra?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static string FormatRateLimitHeaders(HttpResponseMessage response)
    {
        var parts = new List<string>();
        foreach (var h in response.Headers)
        {
            if (h.Key.Contains("RateLimit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(h.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{h.Key}={string.Join(",", h.Value)}");
            }
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "(none)";
    }
}

internal static class UserAgent
{
    public static readonly string Value = $"WeTrakr-Jellyfin/{typeof(UserAgent).Assembly.GetName().Version}";
}
