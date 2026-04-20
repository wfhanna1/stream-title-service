using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class RestreamTokenProviderTests
{
    private readonly Mock<HttpMessageHandler> _httpHandler = new();
    private readonly Mock<ILogger<RestreamTokenProvider>> _logger = new();

    private RestreamTokenProvider CreateProvider(string refreshToken = "refresh-token",
        string clientId = "client-id", string clientSecret = "client-secret",
        Func<string, Task>? onRefreshTokenUpdated = null,
        ILogger<RestreamTokenProvider>? logger = null)
    {
        var httpClient = new HttpClient(_httpHandler.Object);
        return new RestreamTokenProvider(httpClient, refreshToken, clientId, clientSecret,
            logger: logger, onRefreshTokenUpdated: onRefreshTokenUpdated);
    }

    private void SetupTokenResponseWithRefreshToken(string accessToken, string? newRefreshToken, int expiresIn = 3600)
    {
        var responseBody = newRefreshToken is not null
            ? JsonSerializer.Serialize(new
            {
                access_token = accessToken,
                expires_in = expiresIn,
                refresh_token = newRefreshToken,
                token_type = "Bearer"
            })
            : JsonSerializer.Serialize(new
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
    public async Task GetAccessToken_WhenRestreamReturnsNewRefreshToken_ShouldUpdateInMemory()
    {
        // First call: Restream returns a new refresh token, expires immediately (expiresIn=1, buffer=60 => already expired)
        var callCount = 0;
        string? secondRequestBody = null;

        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken _) =>
            {
                callCount++;
                var body = await req.Content!.ReadAsStringAsync();
                string json;
                if (callCount == 1)
                {
                    json = JsonSerializer.Serialize(new
                    {
                        access_token = "first-access-token",
                        expires_in = 1,
                        refresh_token = "new-rt",
                        token_type = "Bearer"
                    });
                }
                else
                {
                    secondRequestBody = body;
                    json = JsonSerializer.Serialize(new
                    {
                        access_token = "second-access-token",
                        expires_in = 3600,
                        token_type = "Bearer"
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var provider = CreateProvider("original-rt");

        await provider.GetAccessTokenAsync(CancellationToken.None);
        // expiresIn=1 with 60s buffer means token is immediately expired, so second call hits HTTP
        await provider.GetAccessTokenAsync(CancellationToken.None);

        callCount.Should().Be(2);
        secondRequestBody.Should().NotBeNull();
        secondRequestBody.Should().Contain("refresh_token=new-rt");
        secondRequestBody.Should().NotContain("refresh_token=original-rt");
    }

    [Fact]
    public async Task GetAccessToken_WhenRestreamReturnsNewRefreshToken_ShouldInvokeCallback()
    {
        SetupTokenResponseWithRefreshToken("access-token", "new-refresh-token");

        string? callbackArg = null;
        Func<string, Task> callback = token =>
        {
            callbackArg = token;
            return Task.CompletedTask;
        };

        var provider = CreateProvider("original-rt", onRefreshTokenUpdated: callback);

        await provider.GetAccessTokenAsync(CancellationToken.None);

        callbackArg.Should().Be("new-refresh-token");
    }

    [Fact]
    public async Task GetAccessToken_WhenRestreamReturnsSameRefreshToken_ShouldNotInvokeCallback()
    {
        // Response returns the same refresh token as the original
        SetupTokenResponseWithRefreshToken("access-token", "original-rt");

        var callbackInvoked = false;
        Func<string, Task> callback = _ =>
        {
            callbackInvoked = true;
            return Task.CompletedTask;
        };

        var provider = CreateProvider("original-rt", onRefreshTokenUpdated: callback);

        await provider.GetAccessTokenAsync(CancellationToken.None);

        callbackInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task GetAccessToken_WhenRestreamReturnsNoRefreshToken_ShouldNotInvokeCallback()
    {
        // Response has no refresh_token field at all
        SetupTokenResponseWithRefreshToken("access-token", newRefreshToken: null);

        var callbackInvoked = false;
        Func<string, Task> callback = _ =>
        {
            callbackInvoked = true;
            return Task.CompletedTask;
        };

        var provider = CreateProvider("original-rt", onRefreshTokenUpdated: callback);

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        token.Should().Be("access-token");
        callbackInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task GetAccessToken_WhenCallbackThrows_ShouldStillReturnAccessToken()
    {
        SetupTokenResponseWithRefreshToken("access-token", "new-rt");

        Func<string, Task> throwingCallback = _ => throw new Exception("Key Vault unavailable");

        var provider = CreateProvider("original-rt", onRefreshTokenUpdated: throwingCallback);

        var act = () => provider.GetAccessTokenAsync(CancellationToken.None);

        var token = await act.Should().NotThrowAsync();
        token.Subject.Should().Be("access-token");
    }

    [Fact]
    public async Task GetAccessToken_WhenCallbackIsNull_ShouldNotThrow()
    {
        SetupTokenResponseWithRefreshToken("access-token", "new-rt");

        // Construct with null callback explicitly
        var provider = CreateProvider("original-rt", onRefreshTokenUpdated: null);

        var act = () => provider.GetAccessTokenAsync(CancellationToken.None);

        var token = await act.Should().NotThrowAsync();
        token.Subject.Should().Be("access-token");
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

    [Fact]
    public async Task GetAccessToken_HttpFailure_ShouldLogStatusAndBody()
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}", System.Text.Encoding.UTF8, "application/json")
            });

        var provider = CreateProvider(logger: _logger.Object);
        var act = () => provider.GetAccessTokenAsync(CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();

        _logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("401") &&
                    v.ToString()!.Contains("invalid_grant")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }
}
