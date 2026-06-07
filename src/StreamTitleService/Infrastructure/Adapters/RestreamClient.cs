using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Infrastructure.Time;

namespace StreamTitleService.Infrastructure.Adapters;

public class RestreamClient : ITitlePlatformClient
{
    private const string FailedLogPrefix = "StreamTitleFailed";

    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokenProvider;
    private readonly RestreamRetryPolicy _retryPolicy;
    private readonly IDelayProvider _delayProvider;
    private readonly ILogger<RestreamClient>? _logger;

    public RestreamClient(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        RestreamRetryPolicy retryPolicy,
        IDelayProvider delayProvider,
        ILogger<RestreamClient>? logger = null)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _retryPolicy = retryPolicy;
        _delayProvider = delayProvider;
        _logger = logger;
    }

    public async Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct)
    {
        _logger?.LogInformation("Setting stream title: {Title}", title);

        var token = await _tokenProvider.GetAccessTokenAsync(ct);

        // Get all channels
        var getRequest = new HttpRequestMessage(HttpMethod.Get, "user/channel/all");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _httpClient.SendAsync(getRequest, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogError("Failed to fetch channels: HTTP {StatusCode}, body: {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var channels = await response.Content.ReadFromJsonAsync<JsonElement[]>(ct)
            ?? Array.Empty<JsonElement>();

        // Logs property keys so App Insights can reveal if Restream renamed the "enabled" field.
        if (channels.Length > 0)
        {
            var firstChannelKeys = string.Join(", ",
                channels[0].EnumerateObject().Select(p => p.Name));
            _logger?.LogDebug("Restream returned {Count} channel(s). First channel property keys: [{Keys}]",
                channels.Length, firstChannelKeys);
        }
        else
        {
            _logger?.LogWarning("Restream returned an empty channel list");
        }

        var enabledChannels = channels
            .Where(c => c.TryGetProperty("enabled", out var e) && e.GetBoolean())
            .ToList();

        _logger?.LogInformation("Channel filter result: {Total} total, {Enabled} enabled",
            channels.Length, enabledChannels.Count);

        if (enabledChannels.Count == 0)
        {
            _logger?.LogWarning(
                "No enabled channels found on Restream. Total channels: {Total}. " +
                "If Total > 0 but Enabled = 0, the 'enabled' field may have been renamed in the Restream API " +
                "or all channels are explicitly disabled. Check the channel property keys logged above.",
                channels.Length);
            return new TitleUpdateResult(0, 0);
        }

        // Patch and verify each enabled channel
        int updated = 0, failed = 0;
        foreach (var ch in enabledChannels)
        {
            var channelId = ch.GetProperty("id").ToString();
            var name = ch.TryGetProperty("displayName", out var dn) ? dn.GetString() : "unknown";

            var success = await TryUpdateAndVerifyChannelAsync(channelId, name ?? "unknown", title, token, ct);
            if (success) updated++;
            else failed++;
        }

        return new TitleUpdateResult(updated, failed);
    }

    private async Task<bool> TryUpdateAndVerifyChannelAsync(
        string channelId, string channelName, string expectedTitle, string token, CancellationToken ct)
    {
        var attempts = new List<AttemptLog>();

        for (var attempt = 1; attempt <= _retryPolicy.MaxAttempts; attempt++)
        {
            var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"user/channel-meta/{channelId}");
            patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            patchReq.Content = JsonContent.Create(new { title = expectedTitle });
            var patchResp = await _httpClient.SendAsync(patchReq, ct);

            var patchCfRay = ExtractHeader(patchResp.Headers, "cf-ray");
            var patchEtag = ExtractHeader(patchResp.Headers, "etag");

            if (!patchResp.IsSuccessStatusCode)
            {
                var body = await patchResp.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning(
                    "{Prefix}: RestreamPatchFailed channel={Name} channel_id={ChannelId} status={Status} body={Body}",
                    FailedLogPrefix, channelName, channelId, (int)patchResp.StatusCode, body);
                return false;
            }

            if (_retryPolicy.InitialVerifyWait > TimeSpan.Zero)
                await _delayProvider.DelayAsync(_retryPolicy.InitialVerifyWait, ct);

            var getReq = new HttpRequestMessage(HttpMethod.Get, $"user/channel-meta/{channelId}");
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var getResp = await _httpClient.SendAsync(getReq, ct);

            var getCfRay = ExtractHeader(getResp.Headers, "cf-ray");
            var getEtag = ExtractHeader(getResp.Headers, "etag");

            string actualTitle = "";
            if (getResp.IsSuccessStatusCode)
            {
                var meta = await getResp.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (meta.TryGetProperty("title", out var t)) actualTitle = t.GetString() ?? "";
            }

            attempts.Add(new AttemptLog(
                PatchStatus: (int)patchResp.StatusCode,
                PatchCfRay: patchCfRay,
                PatchEtag: patchEtag,
                GetStatus: (int)getResp.StatusCode,
                GetBodyTitle: actualTitle,
                GetCfRay: getCfRay,
                GetEtag: getEtag));

            if (string.Equals(actualTitle, expectedTitle, StringComparison.Ordinal))
            {
                _logger?.LogInformation(
                    "VerifiedChannel channel={Name} channel_id={ChannelId} attempts={Attempt}",
                    channelName, channelId, attempt);
                return true;
            }

            if (attempt < _retryPolicy.MaxAttempts)
            {
                var backoffIndex = Math.Min(attempt - 1, _retryPolicy.BackoffSchedule.Count - 1);
                if (backoffIndex >= 0 && _retryPolicy.BackoffSchedule[backoffIndex] > TimeSpan.Zero)
                    await _delayProvider.DelayAsync(_retryPolicy.BackoffSchedule[backoffIndex], ct);
            }
        }

        var forensics = BuildForensicLogParts(attempts);
        _logger?.LogError(
            "{Prefix}: RestreamVerificationExhausted channel={Name} channel_id={ChannelId} expected={Expected} attempts={Attempts} " +
            "patch_status_per_attempt={PatchStatuses} get_status_per_attempt={GetStatuses} " +
            "patch_cf_ray_per_attempt={PatchCfRays} get_cf_ray_per_attempt={GetCfRays} " +
            "patch_etag_per_attempt={PatchEtags} get_etag_per_attempt={GetEtags} " +
            "get_body_title_per_attempt={GetTitles}",
            FailedLogPrefix, channelName, channelId, expectedTitle, _retryPolicy.MaxAttempts,
            forensics.PatchStatuses, forensics.GetStatuses,
            forensics.PatchCfRays, forensics.GetCfRays,
            forensics.PatchEtags, forensics.GetEtags,
            forensics.GetTitles);
        return false;
    }

    private static string ExtractHeader(System.Net.Http.Headers.HttpHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? string.Join(";", values) : "";

    private static ForensicLogParts BuildForensicLogParts(IReadOnlyList<AttemptLog> attempts) => new(
        PatchStatuses: string.Join(",", attempts.Select(a => a.PatchStatus)),
        GetStatuses:   string.Join(",", attempts.Select(a => a.GetStatus)),
        PatchCfRays:   string.Join(",", attempts.Select(a => a.PatchCfRay)),
        GetCfRays:     string.Join(",", attempts.Select(a => a.GetCfRay)),
        PatchEtags:    string.Join(",", attempts.Select(a => a.PatchEtag)),
        GetEtags:      string.Join(",", attempts.Select(a => a.GetEtag)),
        GetTitles:     string.Join(",", attempts.Select(a => a.GetBodyTitle)));

    private sealed record AttemptLog(
        int PatchStatus,
        string PatchCfRay,
        string PatchEtag,
        int GetStatus,
        string GetBodyTitle,
        string GetCfRay,
        string GetEtag);

    private sealed record ForensicLogParts(
        string PatchStatuses,
        string GetStatuses,
        string PatchCfRays,
        string GetCfRays,
        string PatchEtags,
        string GetEtags,
        string GetTitles);
}
