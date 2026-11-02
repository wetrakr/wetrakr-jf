using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.WeTrakr.Configuration;

namespace Jellyfin.Plugin.WeTrakr.Api;

/// <summary>The API answered 401: the link's token is no longer valid.</summary>
public class InvalidTokenException : Exception
{
    public InvalidTokenException() : base("WeTrakr rejected the token (401). Link the account again.") { }
}

/// <summary>Error response from the API, with its HTTP status and error code.</summary>
public class ApiException : Exception
{
    public ApiException(HttpStatusCode status, string? error, string? message)
        : base(string.IsNullOrEmpty(message) ? (error ?? status.ToString()) : message)
    {
        Status = status;
        Error = error;
    }

    public HttpStatusCode Status { get; }

    public string? Error { get; }
}

/// <summary>Credentials and server details for the sync calls of one linked account.</summary>
public class SyncContext
{
    public string Token { get; set; } = string.Empty;
    public string JellyfinUserId { get; set; } = string.Empty;
    public string JellyfinUserName { get; set; } = string.Empty;
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? ServerVersion { get; set; }

    public static SyncContext From(UserLink link) => new()
    {
        Token = link.WebhookToken,
        JellyfinUserId = link.JellyfinUserId,
        JellyfinUserName = link.JellyfinUserName
    };
}

/// <summary>
/// Client of the library sync endpoints (/integrations/jellyfin/*). The token
/// is the one the account got when it was paired, the same as the webhook's.
/// </summary>
public class SyncApi
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _factory;

    public SyncApi(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    private static string BaseUrl
    {
        get
        {
            var url = Plugin.Instance?.Configuration?.ApiBaseUrl;
            if (string.IsNullOrWhiteSpace(url)) url = "https://api.wetrakr.com";
            return url.TrimEnd('/');
        }
    }

    public Task<SyncStatusResponse?> GetStatus(SyncContext ctx, CancellationToken ct)
        => Send<SyncStatusResponse>(HttpMethod.Get, "/integrations/jellyfin/status", null, ctx, ct);

    public Task<ImportAccepted?> PostImport(SyncContext ctx, ImportBatch batch, CancellationToken ct)
        => Send<ImportAccepted>(HttpMethod.Post, "/integrations/jellyfin/import", batch, ctx, ct);

    public Task<ImportJobStatus?> GetImportJob(SyncContext ctx, string jobId, CancellationToken ct)
        => Send<ImportJobStatus>(HttpMethod.Get, "/integrations/jellyfin/import/" + Uri.EscapeDataString(jobId), null, ctx, ct);

    public Task<SnapshotPage?> GetSnapshot(SyncContext ctx, string section, string? cursor, CancellationToken ct)
    {
        var path = "/integrations/jellyfin/snapshot?section=" + section
            + (string.IsNullOrEmpty(cursor) ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
        return Send<SnapshotPage>(HttpMethod.Get, path, null, ctx, ct);
    }

    /// <summary>
    /// Changes since `since`. Without `full` the API answers `unchanged` after a
    /// cheap check when there is nothing new: this is the polling call. With
    /// `full` it always reads (complete pass).
    /// </summary>
    public Task<ChangesPage?> GetChanges(SyncContext ctx, DateTime since, bool full, CancellationToken ct)
    {
        var path = "/integrations/jellyfin/changes?since=" + Uri.EscapeDataString(since.ToUniversalTime().ToString("o"))
            + (full ? "&full=1" : string.Empty);
        return Send<ChangesPage>(HttpMethod.Get, path, null, ctx, ct);
    }

    private async Task<T?> Send<T>(HttpMethod method, string path, object? body, SyncContext ctx, CancellationToken ct)
        where T : class
    {
        using var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.UserAgent.ParseAdd(UserAgent.Value);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Token);
        req.Headers.TryAddWithoutValidation("X-Jellyfin-User-Id", ctx.JellyfinUserId);
        req.Headers.TryAddWithoutValidation("X-Jellyfin-User-Name", ctx.JellyfinUserName ?? string.Empty);
        if (!string.IsNullOrEmpty(ctx.ServerId)) req.Headers.TryAddWithoutValidation("X-Jellyfin-Server-Id", ctx.ServerId);
        if (!string.IsNullOrEmpty(ctx.ServerName)) req.Headers.TryAddWithoutValidation("X-Jellyfin-Server-Name", ctx.ServerName);
        if (!string.IsNullOrEmpty(ctx.ServerVersion)) req.Headers.TryAddWithoutValidation("X-Jellyfin-Server-Version", ctx.ServerVersion);
        req.Headers.TryAddWithoutValidation("X-WeTrakr-Plugin-Version", UserAgent.PluginVersion);
        if (body != null)
        {
            // With Content-Length (not chunked): some proxies insist on it.
            var json = JsonSerializer.Serialize(body, body.GetType(), Json);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var http = _factory.CreateClient(HttpClientNames.WeTrakr);
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidTokenException();
        if (!res.IsSuccessStatusCode)
        {
            ApiError? err = null;
            try { err = string.IsNullOrEmpty(text) ? null : JsonSerializer.Deserialize<ApiError>(text, Json); }
            catch (JsonException) { /* body is not JSON */ }
            throw new ApiException(res.StatusCode, err?.Error, err?.Message);
        }
        return string.IsNullOrEmpty(text) ? null : JsonSerializer.Deserialize<T>(text, Json);
    }

    /// <summary>Import item in the API's format: optional fields only travel when they say something.</summary>
    public static Dictionary<string, object?> ToPayload(ImportItem it)
    {
        var d = new Dictionary<string, object?>
        {
            ["type"] = it.Type,
            ["ids"] = it.Ids,
            ["title"] = it.Title,
            ["played"] = it.Played,
            ["new_play"] = it.NewPlay,
            ["play_count"] = it.PlayCount,
            ["position_ticks"] = it.PositionTicks,
            ["runtime_ticks"] = it.RuntimeTicks
        };
        if (it.SeriesIds != null) d["series_ids"] = it.SeriesIds;
        if (it.Season != null) d["season"] = it.Season;
        if (it.Episode != null) d["episode"] = it.Episode;
        if (it.SeriesTitle != null) d["series_title"] = it.SeriesTitle;
        if (it.Year != null) d["year"] = it.Year;
        if (it.LastPlayedAt != null) d["last_played_at"] = DateTime.SpecifyKind(it.LastPlayedAt.Value, DateTimeKind.Utc).ToString("o");
        if (it.IsFavorite != null) d["is_favorite"] = it.IsFavorite;
        if (it.RatingSet) d["rating"] = it.Rating;
        return d;
    }
}
