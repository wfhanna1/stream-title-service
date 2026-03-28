using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class RestreamClientTests
{
    private readonly Mock<ITokenProvider> _tokenProvider = new();
    private readonly Mock<HttpMessageHandler> _httpHandler = new();

    private RestreamClient CreateClient()
    {
        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-access-token");

        var httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };

        return new RestreamClient(httpClient, _tokenProvider.Object);
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
        SetupPatchChannel(HttpStatusCode.OK);

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

        var client = new RestreamClient(httpClient, _tokenProvider.Object);
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

    private void SetupPatchChannel(HttpStatusCode status)
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Patch),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status));
    }
}
