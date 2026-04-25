using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;

namespace StreamTitleService.Infrastructure.Adapters;

public class RestreamClient : ITitlePlatformClient
{
    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger<RestreamClient>? _logger;

    public RestreamClient(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        ILogger<RestreamClient>? logger = null)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
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

        // Patch each enabled channel
        int updated = 0, failed = 0;
        foreach (var ch in enabledChannels)
        {
            var channelId = ch.GetProperty("id").ToString();
            var name = ch.TryGetProperty("displayName", out var dn) ? dn.GetString() : "unknown";

            var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"user/channel-meta/{channelId}");
            patchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            patchRequest.Content = JsonContent.Create(new { title });
            var patchResponse = await _httpClient.SendAsync(patchRequest, ct);

            if (patchResponse.IsSuccessStatusCode)
            {
                updated++;
                _logger?.LogInformation("Updated channel {Name} ({Id})", name, channelId);
            }
            else
            {
                failed++;
                var errorBody = await patchResponse.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning("Failed to update channel {Name}: {Status}, body: {Body}",
                    name, patchResponse.StatusCode, errorBody);
            }
        }

        return new TitleUpdateResult(updated, failed);
    }
}
