using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;

namespace StreamTitleService.Infrastructure.Adapters;

public class RestreamTokenProvider : ITokenProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _refreshToken;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly ILogger<RestreamTokenProvider>? _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile string? _cachedAccessToken;
    private long _expiresAtTicks = DateTimeOffset.MinValue.UtcTicks;

    private const int ExpiryBufferSeconds = 60;
    private const string TokenEndpoint = "https://api.restream.io/oauth/token";

    public RestreamTokenProvider(
        HttpClient httpClient,
        string refreshToken,
        string clientId,
        string clientSecret,
        ILogger<RestreamTokenProvider>? logger = null)
    {
        _httpClient = httpClient;
        _refreshToken = refreshToken;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // Fast path: return cached token if still valid
        var cachedToken = _cachedAccessToken;
        if (cachedToken is not null && DateTimeOffset.UtcNow.UtcTicks < Volatile.Read(ref _expiresAtTicks))
            return cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check inside the lock
            if (_cachedAccessToken is not null && DateTimeOffset.UtcNow.UtcTicks < Volatile.Read(ref _expiresAtTicks))
                return _cachedAccessToken;

            _logger?.LogInformation("Refreshing Restream access token");

            var formContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", _refreshToken),
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("client_secret", _clientSecret)
            });

            var response = await _httpClient.PostAsync(TokenEndpoint, formContent, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json)
                ?? throw new InvalidOperationException("Failed to deserialize token response");

            Volatile.Write(ref _expiresAtTicks, DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn - ExpiryBufferSeconds).UtcTicks);
            _cachedAccessToken = tokenResponse.AccessToken;

            _logger?.LogInformation("Restream token refreshed, expires in {ExpiresIn}s", tokenResponse.ExpiresIn);

            return _cachedAccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = "";
    }
}
