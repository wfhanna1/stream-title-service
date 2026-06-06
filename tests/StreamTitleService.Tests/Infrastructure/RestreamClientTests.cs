using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Infrastructure.Adapters;
using StreamTitleService.Tests.TestDoubles;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class RestreamClientTests
{
    private readonly Mock<ITokenProvider> _tokenProvider = new();
    private readonly Mock<HttpMessageHandler> _httpHandler = new();
    private readonly Mock<ILogger<RestreamClient>> _logger = new();
    private HttpClient _httpClient = null!;

    private RestreamClient CreateClient(ILogger<RestreamClient>? logger = null)
    {
        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-access-token");

        _httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };

        // MaxAttempts=1 keeps existing tests' behavior; verification runs once with zero delay.
        // Tests that PATCH a specific title and expect success pass verifyTitle to SetupPatchChannel
        // so the verification GET mirrors the same title. See Task 2 of the verify-and-retry plan.
        var policy = new RestreamRetryPolicy(
            MaxAttempts: 1,
            InitialVerifyWait: TimeSpan.Zero,
            BackoffSchedule: Array.Empty<TimeSpan>());
        var delays = new RecordingDelayProvider();

        return new RestreamClient(_httpClient, _tokenProvider.Object, policy, delays, logger);
    }

    [Fact]
    public async Task SetTitle_WithEnabledChannels_ShouldPatchEach()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 },
            new { id = "ch2", displayName = "Twitch", enabled = true, streamingPlatformId = 1 },
            new { id = "ch3", displayName = "Facebook", enabled = false, streamingPlatformId = 37 }
        };

        SetupGetChannels(channels);
        SetupPatchChannel(HttpStatusCode.OK, verifyTitle: "Test Title");

        var client = CreateClient();
        var result = await client.SetTitleAsync("Test Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(2); // Only enabled channels
        result.ChannelsFailed.Should().Be(0);
    }

    [Fact]
    public async Task SetTitle_NoEnabledChannels_ShouldReturnZero()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = false, streamingPlatformId = 5 }
        };

        SetupGetChannels(channels);

        var client = CreateClient();
        var result = await client.SetTitleAsync("Test", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
    }

    [Fact]
    public async Task SetTitle_Auth401_ShouldThrow()
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("channel/all")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("expired-token");

        var httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };

        var client = new RestreamClient(
            httpClient,
            _tokenProvider.Object,
            new RestreamRetryPolicy(MaxAttempts: 1, InitialVerifyWait: TimeSpan.Zero, BackoffSchedule: Array.Empty<TimeSpan>()),
            new RecordingDelayProvider());
        var act = () => client.SetTitleAsync("Test", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private void SetupGetChannels(object channels)
    {
        var json = JsonSerializer.Serialize(channels);
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("channel/all")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task SetTitle_TokenProviderThrows_ShouldPropagate()
    {
        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Token store unavailable"));

        var httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };
        var client = new RestreamClient(
            httpClient,
            _tokenProvider.Object,
            new RestreamRetryPolicy(MaxAttempts: 1, InitialVerifyWait: TimeSpan.Zero, BackoffSchedule: Array.Empty<TimeSpan>()),
            new RecordingDelayProvider());

        var act = () => client.SetTitleAsync("Test", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Token store unavailable");
    }

    [Fact]
    public async Task SetTitle_MixedPatchResults_ShouldReturnCorrectCounts()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 },
            new { id = "ch2", displayName = "Twitch", enabled = true, streamingPlatformId = 1 },
            new { id = "ch3", displayName = "Facebook", enabled = true, streamingPlatformId = 37 }
        };

        SetupGetChannels(channels);

        // Counter-based sequential PATCH responses: 200, 500, 204
        var callCount = 0;
        var statuses = new[] { HttpStatusCode.OK, HttpStatusCode.InternalServerError, HttpStatusCode.NoContent };
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Patch),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                var status = statuses[callCount++];
                return Task.FromResult(new HttpResponseMessage(status));
            });

        // Verification GET for the channels that PATCH succeeds on (ch1 and ch3) returns the title.
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.Contains("/user/channel-meta/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { title = "Test Title", description = (string?)null })
            });

        var client = CreateClient();
        var result = await client.SetTitleAsync("Test Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(2); // 200 and 204 are success
        result.ChannelsFailed.Should().Be(1);  // 500 is failure
    }

    [Fact]
    public async Task SetTitle_GetChannelsReturnsMalformedJson_ShouldThrow()
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("channel/all")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json")
            });

        var client = CreateClient();
        var act = () => client.SetTitleAsync("Test", CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task SetTitle_ChannelMissingEnabledProperty_ShouldSkipChannel()
    {
        // Channel JSON has id and displayName but no "enabled" key
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube" }
        };

        SetupGetChannels(channels);

        var client = CreateClient();
        var result = await client.SetTitleAsync("Test", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
        result.ChannelsFailed.Should().Be(0);
    }

    [Fact]
    public async Task SetTitle_ChannelMissingEnabledProperty_ShouldLogWarningWithPropertyKeys()
    {
        // Simulates the case where Restream renamed the "enabled" field (e.g. to "isEnabled").
        // The diagnostic warning log should include the actual property keys returned by the API.
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", isEnabled = true }
        };

        SetupGetChannels(channels);

        var client = CreateClient(_logger.Object);
        var result = await client.SetTitleAsync("Test", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);

        _logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("enabled") &&
                    v.ToString()!.Contains("renamed")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }

    [Fact]
    public async Task SetTitle_NetworkTimeout_ShouldThrow()
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection timeout"));

        var client = CreateClient();
        var act = () => client.SetTitleAsync("Test", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("Connection timeout");
    }

    private void SetupPatchChannel(HttpStatusCode status, string? verifyTitle = null)
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Patch),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status));

        // Mirror the PATCHed title in the verification GET so the post-PATCH
        // verification step in RestreamClient sees a match for happy-path tests.
        if (verifyTitle is not null)
        {
            _httpHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r =>
                        r.Method == HttpMethod.Get &&
                        r.RequestUri!.PathAndQuery.Contains("/user/channel-meta/")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { title = verifyTitle, description = (string?)null })
                });
        }
    }

    // Fix #2: per-request headers instead of DefaultRequestHeaders (thread-safe)
    [Fact]
    public async Task SetTitle_ShouldNotMutateDefaultRequestHeaders()
    {
        // Verifies fix #2: per-request headers instead of DefaultRequestHeaders
        SetupGetChannels(new[] { new { id = "ch1", displayName = "YT", enabled = true, streamingPlatformId = 5 } });
        SetupPatchChannel(HttpStatusCode.OK, verifyTitle: "Test");

        var client = CreateClient();
        await client.SetTitleAsync("Test", CancellationToken.None);

        // DefaultRequestHeaders should NOT have Authorization set
        _httpClient.DefaultRequestHeaders.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task SetTitle_ShouldLogEntryWithTitle()
    {
        SetupGetChannels(new[] { new { id = "ch1", displayName = "YT", enabled = true, streamingPlatformId = 5 } });
        SetupPatchChannel(HttpStatusCode.OK, verifyTitle: "Sunday Liturgy");

        var client = CreateClient(_logger.Object);
        await client.SetTitleAsync("Sunday Liturgy", CancellationToken.None);

        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Sunday Liturgy")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());
    }

    [Fact]
    public async Task SetTitle_ChannelFetchFails_ShouldLogResponseBody()
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("channel/all")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":\"access_denied\"}", System.Text.Encoding.UTF8, "application/json")
            });

        var client = CreateClient(_logger.Object);
        var act = () => client.SetTitleAsync("Test", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();

        _logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("403") &&
                    v.ToString()!.Contains("access_denied")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }

    [Fact]
    public async Task SetTitle_PatchFails_ShouldLogResponseBody()
    {
        SetupGetChannels(new[] { new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 } });

        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Patch),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"server_error\"}", System.Text.Encoding.UTF8, "application/json")
            });

        var client = CreateClient(_logger.Object);
        var result = await client.SetTitleAsync("Test", CancellationToken.None);

        result.ChannelsFailed.Should().Be(1);

        _logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("YouTube") &&
                    v.ToString()!.Contains("server_error")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }
}
