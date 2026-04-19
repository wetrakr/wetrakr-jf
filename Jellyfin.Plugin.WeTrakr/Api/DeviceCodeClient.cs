using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WeTrakr.Api;

/// <summary>
/// Talks to the WeTrakr device-code OAuth endpoints:
///   POST /oauth/device/code?platform=jellyfin -> issues a user_code and device_code
///   POST /oauth/device/token                 -> exchanges a device_code for an access_token
///
/// Pattern mirrors wetrakr-kodi/resources/lib/auth.py. The returned access_token
/// IS the webhook token for Jellyfin — the backend writes it into
/// auth.connections.jellyfin.webhook_token on activation.
/// </summary>
public class DeviceCodeClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<DeviceCodeClient> _logger;

    public DeviceCodeClient(IHttpClientFactory factory, ILogger<DeviceCodeClient> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<DeviceCodeResponse?> RequestCodeAsync(string apiBaseUrl, CancellationToken ct)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/oauth/device/code?platform=jellyfin";
        var http = _factory.CreateClient(HttpClientNames.WeTrakr);

        try
        {
            var response = await http.PostAsync(url, content: null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeTrakr] Device code request failed");
            return null;
        }
    }

    public async Task<DeviceTokenResponse> PollTokenAsync(string apiBaseUrl, string deviceCode, CancellationToken ct)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/oauth/device/token";
        var http = _factory.CreateClient(HttpClientNames.WeTrakr);

        try
        {
            var response = await http.PostAsJsonAsync(url, new { device_code = deviceCode }, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<DeviceTokenResponse>(cancellationToken: ct).ConfigureAwait(false)
                    ?? new DeviceTokenResponse { Error = "invalid_response" };
            }

            // 400 responses carry a JSON error body: authorization_pending, expired_token, etc.
            var err = await response.Content.ReadFromJsonAsync<DeviceTokenResponse>(cancellationToken: ct).ConfigureAwait(false);
            return err ?? new DeviceTokenResponse { Error = "http_error" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeTrakr] Device token poll failed");
            return new DeviceTokenResponse { Error = "network_error" };
        }
    }
}

public class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_url")]
    public string VerificationUrl { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

public class DeviceTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
