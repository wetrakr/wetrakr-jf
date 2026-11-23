using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        // Asks WeTrakr to answer once the event is processed, with what came of
        // it (matched, not found, ignored and why), so the log can say so.
        http.DefaultRequestHeaders.Add("X-WeTrakr-Result", "1");

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

                var result = await ReadResultAsync(response, ct).ConfigureAwait(false);
                LogSent(target, payload, result);
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
    /// One line per event that reached WeTrakr, with the ids it was sent with
    /// and, from API versions that report it, what WeTrakr made of it: the
    /// title it matched, or why it did not record anything. When a title lands
    /// on the wrong entry (or nowhere), the ids are what to check in Jellyfin's
    /// metadata. Progress ticks only at Debug.
    /// </summary>
    private void LogSent(ScrobbleTarget target, ScrobblePayload payload, EventResult? result)
    {
        var item = ScrobbleLog.Describe(payload);
        var ids = ScrobbleLog.Ids(payload);
        var user = string.IsNullOrEmpty(payload.UserName) ? payload.UserId : payload.UserName;
        var account = string.IsNullOrEmpty(target.Link?.WeTrakrUsername) ? "WeTrakr" : target.Link!.WeTrakrUsername;

        switch (result?.Status)
        {
            case "failed":
                _logger.LogWarning(
                    "[WeTrakr] WeTrakr could not record {Event} for {Item} ({Ids}) from Jellyfin user {User}: {Reason}",
                    payload.Event, item, ids, user, result.Reason ?? "no reason given");
                return;
            case "ignored":
                _logger.LogInformation(
                    "[WeTrakr] WeTrakr ignored {Event} for {Item} from Jellyfin user {User}: {Reason}",
                    payload.Event, item, user, result.Reason ?? "no reason given");
                return;
        }

        var level = payload.Event == "PlaybackProgress" ? LogLevel.Debug : LogLevel.Information;
        if (!_logger.IsEnabled(level)) return;

        var matched = result?.Media == null ? string.Empty : $"; matched to {result.Describe()}";
        var already = result?.Status == "duplicate" ? " (already recorded)" : string.Empty;
        _logger.Log(
            level,
            "[WeTrakr] Sent {Event} for {Item} ({Ids}) from Jellyfin user {User} to {Account}{Matched}{Already}",
            payload.Event, item, ids, user, account, matched, already);
    }

    /// <summary>The "result" WeTrakr sends back when asked; null from older API versions.</summary>
    private static async Task<EventResult?> ReadResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<EventResponse>(cancellationToken: ct).ConfigureAwait(false);
            return body?.Result;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
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

/// <summary>Body of the webhook answer: <c>{ ok, result? }</c>.</summary>
internal sealed class EventResponse
{
    [JsonPropertyName("result")]
    public EventResult? Result { get; set; }
}

/// <summary>What WeTrakr did with one event (status, reason, what it matched).</summary>
internal sealed class EventResult
{
    /// <summary>success, duplicate, ignored, failed, accepted or pending.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("media")]
    public EventResultMedia? Media { get; set; }

    [JsonPropertyName("episode")]
    public EventResultEpisode? Episode { get; set; }

    /// <summary>"Title" S01E02 (WeTrakr id 123).</summary>
    public string Describe()
    {
        var text = $"\"{Media?.Title}\"";
        if (Episode != null) text += $" S{Episode.SeasonNumber:D2}E{Episode.Number:D2}";
        if (Media?.Id is { } id && id.ValueKind != JsonValueKind.Null) text += $" (WeTrakr {Media.Type} {id})";
        return text;
    }
}

internal sealed class EventResultMedia
{
    /// <summary>Number or string, whatever the API sends: only printed.</summary>
    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

internal sealed class EventResultEpisode
{
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }
}
