using Azure.Storage.Blobs;
using FluentAssertions;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Integration;

/// <summary>
/// Integration tests against the real Azure Blob Storage account.
/// Verifies that BlobStorageYouTubeTokenProvider can load token.json and
/// construct a YouTubeService. Does NOT make any YouTube API calls.
/// Requires INTEGRATION_TEST_STORAGE_CONNECTION to be set.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class BlobStorageIntegrationTests
{
    private static readonly string? StorageConnection =
        Environment.GetEnvironmentVariable("INTEGRATION_TEST_STORAGE_CONNECTION");

    [SkippableFact]
    public async Task LoadYouTubeToken_FromRealBlobStorage_ShouldReturnYouTubeService()
    {
        Skip.If(string.IsNullOrWhiteSpace(StorageConnection),
            "INTEGRATION_TEST_STORAGE_CONNECTION is not set; skipping integration test.");

        var blobClient = new BlobClient(StorageConnection, "youtube-tokens", "token.json");
        var provider = new BlobStorageYouTubeTokenProvider(blobClient);

        var service = await provider.CreateYouTubeServiceAsync(CancellationToken.None);

        service.Should().NotBeNull("a YouTubeService should be constructed from the stored token");
        // Not making any YouTube API calls -- just verifying the service was created successfully
    }
}
