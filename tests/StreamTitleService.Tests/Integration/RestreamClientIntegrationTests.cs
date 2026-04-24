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
/// Requires INTEGRATION_TEST_KV_URI to be set and az login to be active.
/// Run with: dotnet test --filter "Category=Integration"
///
/// To test title updates against a live event, also set:
///   INTEGRATION_TEST_TITLE="Friday, April 24, 2026 - Arabic Bible Study"
/// This sets the title to exactly what is already live, so it is idempotent and safe to run
/// during a live stream.
/// </summary>
[Trait("Category", "Integration")]
public class RestreamClientIntegrationTests
{
    private const string RestreamBaseUrl = "https://api.restream.io/v2/";

    private static readonly string? KvUri =
        Environment.GetEnvironmentVariable("INTEGRATION_TEST_KV_URI");

    private static readonly string? TestTitle =
        Environment.GetEnvironmentVariable("INTEGRATION_TEST_TITLE");

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<(RestreamTokenProvider tokenProvider, string accessToken)>
        BuildTokenProviderAsync()
    {
        var kvClient = new SecretClient(new Uri(KvUri!), new DefaultAzureCredential());
        var refreshToken = (await kvClient.GetSecretAsync("restream-refresh-token")).Value.Value;
        var clientId = (await kvClient.GetSecretAsync("restream-client-id")).Value.Value;
        var clientSecret = (await kvClient.GetSecretAsync("restream-client-secret")).Value.Value;

        var provider = new RestreamTokenProvider(
            new HttpClient(),
            refreshToken,
            clientId,
            clientSecret);

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);
        return (provider, token);
    }

    // ------------------------------------------------------------------
    // Test 1: GET /user/channel/all — validates token + channel structure
    // ------------------------------------------------------------------

    [SkippableFact]
    public async Task GetChannels_WithRealToken_ShouldReturnValidJsonArray()
    {
        Skip.If(string.IsNullOrWhiteSpace(KvUri),
            "INTEGRATION_TEST_KV_URI is not set; skipping integration test.");

        var (_, token) = await BuildTokenProviderAsync();

        using var httpClient = new HttpClient { BaseAddress = new Uri(RestreamBaseUrl) };
        var request = new HttpRequestMessage(HttpMethod.Get, "user/channel/all");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await httpClient.SendAsync(request, CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"GET /user/channel/all should succeed; actual status: {response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace("response body should contain channel data");

        var channels = JsonSerializer.Deserialize<JsonElement[]>(body);
        channels.Should().NotBeNull("the response should deserialize to a JSON array");
        channels!.Length.Should().BeGreaterThan(0, "the account should have at least one channel configured");

        // Validate that each channel has the fields the RestreamClient depends on.
        // If any assertion fails here, it means the Restream API changed its response schema
        // and the 'enabled' filter in RestreamClient.SetTitleAsync will silently drop all channels.
        foreach (var ch in channels)
        {
            var props = ch.EnumerateObject().Select(p => p.Name).ToList();

            ch.TryGetProperty("id", out _).Should().BeTrue(
                $"channel should have an 'id' field; got properties: [{string.Join(", ", props)}]");

            ch.TryGetProperty("enabled", out _).Should().BeTrue(
                $"channel should have an 'enabled' field; got properties: [{string.Join(", ", props)}]. " +
                "If Restream renamed this field, the SetTitleAsync enabled-filter silently drops all channels.");
        }

        var enabledCount = channels.Count(c =>
            c.TryGetProperty("enabled", out var e) && e.GetBoolean());
        enabledCount.Should().BeGreaterThan(0,
            "at least one channel must be enabled for SetTitleAsync to update anything");
    }

    // ------------------------------------------------------------------
    // Test 2: SetTitleAsync — end-to-end PATCH against live Restream API
    //
    // Set INTEGRATION_TEST_TITLE to the idempotent title already live on
    // Restream so this test is safe to run during an active stream.
    // Example: INTEGRATION_TEST_TITLE="Friday, April 24, 2026 - Arabic Bible Study"
    // ------------------------------------------------------------------

    [SkippableFact]
    public async Task SetTitle_WithRealToken_ShouldUpdateAllEnabledChannels()
    {
        Skip.If(string.IsNullOrWhiteSpace(KvUri),
            "INTEGRATION_TEST_KV_URI is not set; skipping integration test.");
        Skip.If(string.IsNullOrWhiteSpace(TestTitle),
            "INTEGRATION_TEST_TITLE is not set; skipping SetTitle integration test. " +
            "Set it to the current live title (e.g. 'Friday, April 24, 2026 - Arabic Bible Study') " +
            "to safely test the PATCH path without changing the stream title.");

        var (provider, _) = await BuildTokenProviderAsync();

        using var httpClient = new HttpClient { BaseAddress = new Uri(RestreamBaseUrl) };
        var client = new RestreamClient(httpClient, provider);

        var result = await client.SetTitleAsync(TestTitle!, CancellationToken.None);

        result.ChannelsUpdated.Should().BeGreaterThan(0,
            $"SetTitleAsync should update at least one enabled channel. " +
            $"Got ChannelsUpdated={result.ChannelsUpdated}, ChannelsFailed={result.ChannelsFailed}. " +
            "If ChannelsUpdated=0 and ChannelsFailed=0 the 'enabled' filter dropped all channels " +
            "(Restream may have renamed the field). " +
            "If ChannelsFailed>0, check the App Insights logs for the HTTP error body on the PATCH.");

        result.ChannelsFailed.Should().Be(0,
            $"No channel PATCH should fail. " +
            $"Got ChannelsFailed={result.ChannelsFailed}. " +
            "Check App Insights for the HTTP status and response body logged at Warning level.");
    }
}
