using Azure.Storage.Blobs;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace StreamTitleService.Infrastructure.Adapters;

/// <summary>
/// Loads YouTube OAuth2 credentials from Azure Blob Storage.
///
/// The Python YT-Title-Updater uses token.pickle (Python-specific binary format).
/// For C#, we store the Google OAuth credentials as JSON in Blob Storage instead:
/// {
///   "access_token": "...",
///   "refresh_token": "...",
///   "client_id": "...",
///   "client_secret": "...",
///   "token_uri": "https://oauth2.googleapis.com/token"
/// }
///
/// GoogleWebAuthorizationBroker handles token refresh automatically.
/// </summary>
public class BlobStorageYouTubeTokenProvider
{
    private readonly BlobClient _blobClient;
    private readonly ILogger<BlobStorageYouTubeTokenProvider>? _logger;

    public BlobStorageYouTubeTokenProvider(
        BlobClient blobClient,
        ILogger<BlobStorageYouTubeTokenProvider>? logger = null)
    {
        _blobClient = blobClient;
        _logger = logger;
    }

    public async Task<YouTubeService> CreateYouTubeServiceAsync(CancellationToken ct)
    {
        // Download token JSON from blob storage
        var downloadResult = await _blobClient.DownloadContentAsync(ct);
        var json = downloadResult.Value.Content.ToString();

        var tokenData = JsonSerializer.Deserialize<YouTubeTokenData>(json)
            ?? throw new InvalidOperationException("Failed to deserialize YouTube token from blob storage");

        _logger?.LogInformation("Loaded YouTube OAuth token from blob storage");

        // Build UserCredential from stored token
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = tokenData.ClientId,
                ClientSecret = tokenData.ClientSecret
            },
            Scopes = new[] { YouTubeService.Scope.Youtube }
        });

        var token = new TokenResponse
        {
            AccessToken = tokenData.AccessToken,
            RefreshToken = tokenData.RefreshToken
        };

        var credential = new UserCredential(flow, "user", token);

        // The credential will auto-refresh if expired
        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "stream-title-service"
        });
    }

    private class YouTubeTokenData
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("client_id")]
        public string ClientId { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("client_secret")]
        public string ClientSecret { get; set; } = "";
    }
}
