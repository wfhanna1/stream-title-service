using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Integration;

/// <summary>
/// Integration tests that hit the real Restream OAuth token endpoint.
/// Credentials are loaded from the real Key Vault using DefaultAzureCredential.
/// Requires INTEGRATION_TEST_KV_URI to be set and az login to be active.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class RestreamTokenProviderIntegrationTests
{
    private static readonly string? KvUri =
        Environment.GetEnvironmentVariable("INTEGRATION_TEST_KV_URI");

    [SkippableFact]
    public async Task GetAccessToken_WithRealCredentials_ShouldReturnValidToken()
    {
        Skip.If(string.IsNullOrWhiteSpace(KvUri),
            "INTEGRATION_TEST_KV_URI is not set; skipping integration test.");

        // Load credentials from Key Vault using the logged-in az identity
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

        token.Should().NotBeNullOrWhiteSpace("a valid access token should be returned");
        token.Length.Should().BeGreaterThan(10, "a real access token is never trivially short");
    }
}
