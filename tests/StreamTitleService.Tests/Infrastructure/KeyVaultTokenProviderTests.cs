using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class KeyVaultTokenProviderTests
{
    private readonly Mock<HttpMessageHandler> _httpHandler = new();

    private KeyVaultTokenProvider CreateProvider(string refreshToken = "refresh-token",
        string clientId = "client-id", string clientSecret = "client-secret")
    {
        var httpClient = new HttpClient(_httpHandler.Object);
        return new KeyVaultTokenProvider(httpClient, refreshToken, clientId, clientSecret);
    }

    private void SetupTokenResponse(string accessToken, int expiresIn = 3600)
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            expires_in = expiresIn,
            token_type = "Bearer"
        });

        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Post &&
                    r.RequestUri!.ToString().Contains("api.restream.io/oauth/token")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task GetAccessTokenAsync_FirstCall_ShouldPostToRestreamAndReturnToken()
    {
        SetupTokenResponse("new-access-token");
        var provider = CreateProvider();

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        token.Should().Be("new-access-token");
        _httpHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Method == HttpMethod.Post &&
                r.RequestUri!.ToString() == "https://api.restream.io/oauth/token"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessTokenAsync_SecondCall_ShouldReturnCachedTokenWithoutHttpCall()
    {
        SetupTokenResponse("new-access-token", expiresIn: 3600);
        var provider = CreateProvider();

        var firstToken = await provider.GetAccessTokenAsync(CancellationToken.None);
        var secondToken = await provider.GetAccessTokenAsync(CancellationToken.None);

        firstToken.Should().Be("new-access-token");
        secondToken.Should().Be("new-access-token");

        // HTTP should only have been called once despite two GetAccessTokenAsync calls
        _httpHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessTokenAsync_ShouldPostFormEncodedBodyWithGrantType()
    {
        string? capturedBody = null;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken _) =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                var json = JsonSerializer.Serialize(new
                {
                    access_token = "captured-token",
                    expires_in = 3600,
                    token_type = "Bearer"
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var provider = CreateProvider("my-refresh", "my-client-id", "my-client-secret");
        await provider.GetAccessTokenAsync(CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("grant_type=refresh_token");
        capturedBody.Should().Contain("refresh_token=my-refresh");
        capturedBody.Should().Contain("client_id=my-client-id");
        capturedBody.Should().Contain("client_secret=my-client-secret");
    }

    [Fact]
    public async Task GetAccessToken_Http401_ShouldThrow()
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var provider = CreateProvider();
        var act = () => provider.GetAccessTokenAsync(CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetAccessToken_ShortExpiry_ShouldRefreshImmediatelyOnNextCall()
    {
        // expiresIn=30 means expiry buffer of 60 puts effective expiry at now-30s (already expired)
        SetupTokenResponse("short-lived-token", expiresIn: 30);

        var provider = CreateProvider();

        var firstToken = await provider.GetAccessTokenAsync(CancellationToken.None);
        var secondToken = await provider.GetAccessTokenAsync(CancellationToken.None);

        firstToken.Should().Be("short-lived-token");
        secondToken.Should().Be("short-lived-token");

        // Both calls should have triggered an HTTP request since token was expired immediately
        _httpHandler.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessToken_ConcurrentCalls_ShouldOnlyRefreshOnce()
    {
        // Add a small delay so concurrent calls bunch up at the semaphore
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async () =>
            {
                await Task.Delay(50);
                var json = JsonSerializer.Serialize(new
                {
                    access_token = "concurrent-token",
                    expires_in = 3600,
                    token_type = "Bearer"
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var provider = CreateProvider();

        var tokens = await Task.WhenAll(
            provider.GetAccessTokenAsync(CancellationToken.None),
            provider.GetAccessTokenAsync(CancellationToken.None),
            provider.GetAccessTokenAsync(CancellationToken.None));

        tokens.Should().AllBe("concurrent-token");

        // Semaphore should have prevented duplicate refreshes -- exactly one HTTP call
        _httpHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessToken_EmptyResponseBody_ShouldThrow()
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", System.Text.Encoding.UTF8, "application/json")
            });

        var provider = CreateProvider();
        var act = () => provider.GetAccessTokenAsync(CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }
}
