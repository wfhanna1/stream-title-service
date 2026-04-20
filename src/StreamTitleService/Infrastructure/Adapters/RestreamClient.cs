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

        var enabledChannels = channels
            .Where(c => c.TryGetProperty("enabled", out var e) && e.GetBoolean())
            .ToList();

        if (enabledChannels.Count == 0)
        {
            _logger?.LogWarning("No enabled channels found on Restream");
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
