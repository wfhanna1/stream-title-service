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

public class RestreamClientVerifyRetryTests
{
    private readonly Mock<ITokenProvider> _tokenProvider = new();
    private readonly Mock<HttpMessageHandler> _httpHandler = new();
    private readonly Mock<ILogger<RestreamClient>> _logger = new();
    private readonly RecordingDelayProvider _delays = new();

    private RestreamClient CreateClient(RestreamRetryPolicy? policy = null)
    {
        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-access-token");

        var httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };

        return new RestreamClient(
            httpClient,
            _tokenProvider.Object,
            policy ?? FastTestPolicy(),
            _delays,
            _logger.Object);
    }

    private static RestreamRetryPolicy FastTestPolicy() => new(
        MaxAttempts: 3,
        InitialVerifyWait: TimeSpan.Zero,
        BackoffSchedule: new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero });

    private void SetupGetChannels(object channels)
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.EndsWith("/user/channel/all")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(channels)
            });
    }

    private void SetupPatchChannel(string channelId, HttpStatusCode status,
        string? cfRay = "test-cf-ray", string? etag = "W/\"test-etag\"")
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Patch &&
                    r.RequestUri!.PathAndQuery.EndsWith($"/user/channel-meta/{channelId}")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(status)
                {
                    Content = new StringContent(string.Empty)
                };
                if (cfRay is not null) resp.Headers.TryAddWithoutValidation("cf-ray", cfRay);
                if (etag is not null) resp.Headers.TryAddWithoutValidation("etag", etag);
                return resp;
            });
    }

    private void SetupVerifyGetChannel(string channelId, string returnedTitle,
        HttpStatusCode status = HttpStatusCode.OK,
        string? cfRay = "test-cf-ray", string? etag = "W/\"test-etag\"")
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.EndsWith($"/user/channel-meta/{channelId}")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(new { title = returnedTitle, description = (string?)null })
                };
                if (cfRay is not null) resp.Headers.TryAddWithoutValidation("cf-ray", cfRay);
                if (etag is not null) resp.Headers.TryAddWithoutValidation("etag", etag);
                return resp;
            });
    }

    [Fact]
    public async Task SetTitle_VerifyMatchesFirstTry_LogsVerifiedChannelAttempts1AndNoDelays()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.OK);
        SetupVerifyGetChannel("ch1", returnedTitle: "Friday Bible Study");

        var client = CreateClient();

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(0);
        _delays.Recorded.Should().BeEmpty(
            "happy path verifies on first try with InitialVerifyWait=0, no backoff");

        _logger.Invocations
            .Should()
            .Contain(i =>
                i.Method.Name == nameof(ILogger.Log) &&
                (LogLevel)i.Arguments[0] == LogLevel.Information &&
                i.Arguments[2]!.ToString()!.Contains("VerifiedChannel") &&
                i.Arguments[2]!.ToString()!.Contains("ch1") &&
                i.Arguments[2]!.ToString()!.Contains("attempts=1"));
    }
}
