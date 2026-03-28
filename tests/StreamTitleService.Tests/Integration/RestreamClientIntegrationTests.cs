using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Integration;

/// <summary>
/// Integration tests that hit the real Restream API.
/// Only makes safe GET requests -- does NOT call SetTitleAsync so no stream titles are changed.
/// Requires INTEGRATION_TEST_KV_URI to be set and az login to be active.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class RestreamClientIntegrationTests
{
    private const string RestreamBaseUrl = "https://api.restream.io/v2/";

    private static readonly string? KvUri =
        Environment.GetEnvironmentVariable("INTEGRATION_TEST_KV_URI");

    [SkippableFact]
    public async Task GetChannels_WithRealToken_ShouldReturnValidJsonArray()
    {
        Skip.If(string.IsNullOrWhiteSpace(KvUri),
            "INTEGRATION_TEST_KV_URI is not set; skipping integration test.");

        // Load credentials from Key Vault
        var kvClient = new SecretClient(new Uri(KvUri!), new DefaultAzureCredential());
        var refreshToken = (await kvClient.GetSecretAsync("restream-refresh-token")).Value.Value;
        var clientId = (await kvClient.GetSecretAsync("restream-client-id")).Value.Value;
        var clientSecret = (await kvClient.GetSecretAsync("restream-client-secret")).Value.Value;

        var tokenProvider = new RestreamTokenProvider(
            new HttpClient(),
            refreshToken,
            clientId,
            clientSecret);

        var token = await tokenProvider.GetAccessTokenAsync(CancellationToken.None);

        // Make a safe GET to /user/channel/all -- no title changes
        using var httpClient = new HttpClient { BaseAddress = new Uri(RestreamBaseUrl) };
        var request = new HttpRequestMessage(HttpMethod.Get, "user/channel/all");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await httpClient.SendAsync(request, CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"GET /user/channel/all should succeed; actual status: {response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace("response body should contain channel data");

        // Verify the response is a valid JSON array
        var channels = JsonSerializer.Deserialize<JsonElement[]>(body);
        channels.Should().NotBeNull("the response should deserialize to a JSON array");
        channels!.Length.Should().BeGreaterThan(0, "the account should have at least one channel configured");
    }
}
