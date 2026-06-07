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

    private void SetupVerifyGetChannelSequence(string channelId, params string[] titlesInOrder)
    {
        var calls = 0;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.EndsWith($"/user/channel-meta/{channelId}")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var title = titlesInOrder[Math.Min(calls, titlesInOrder.Length - 1)];
                calls++;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { title, description = (string?)null })
                };
                resp.Headers.TryAddWithoutValidation("cf-ray", $"test-cf-ray-{calls}");
                resp.Headers.TryAddWithoutValidation("etag", $"W/\"test-etag-{calls}\"");
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

    [Fact]
    public async Task SetTitle_VerifyStaleOnceThenMatches_SucceedsOnAttempt2()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.OK);
        SetupVerifyGetChannelSequence("ch1", "OLD-TITLE", "Friday Bible Study");

        var policy = new RestreamRetryPolicy(
            MaxAttempts: 3,
            InitialVerifyWait: TimeSpan.FromSeconds(5),
            BackoffSchedule: new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) });

        var client = CreateClient(policy);

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(0);

        // Delays for attempts 1 and 2 of one channel:
        //   attempt 1: wait 5 → GET stale → backoff 5
        //   attempt 2: wait 5 → GET matches → done
        _delays.Recorded.Should().Equal(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        _logger.Invocations
            .Should()
            .Contain(i =>
                (LogLevel)i.Arguments[0] == LogLevel.Information &&
                i.Arguments[2]!.ToString()!.Contains("VerifiedChannel") &&
                i.Arguments[2]!.ToString()!.Contains("attempts=2"));
    }

    [Fact]
    public async Task SetTitle_VerifyStaleThreeTimes_LogsStreamTitleFailedError_AndCountsFailed()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.OK);
        SetupVerifyGetChannelSequence("ch1", "STALE", "STALE", "STALE");

        var client = CreateClient(); // FastTestPolicy: 3 attempts, zero waits

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
        result.ChannelsFailed.Should().Be(1);

        _logger.Invocations
            .Should()
            .Contain(i =>
                (LogLevel)i.Arguments[0] == LogLevel.Error &&
                i.Arguments[2]!.ToString()!.StartsWith("StreamTitleFailed: RestreamVerificationExhausted") &&
                i.Arguments[2]!.ToString()!.Contains("ch1") &&
                i.Arguments[2]!.ToString()!.Contains("attempts=3"));
    }

    [Fact]
    public async Task SetTitle_AlwaysStaleWithConfiguredBackoff_HonorsSchedule()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.OK);
        SetupVerifyGetChannelSequence("ch1", "STALE", "STALE", "STALE");

        var policy = new RestreamRetryPolicy(
            MaxAttempts: 3,
            InitialVerifyWait: TimeSpan.FromSeconds(2),
            BackoffSchedule: new[] { TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) });

        var client = CreateClient(policy);
        await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        //   attempt 1: wait 2 → stale → backoff 4
        //   attempt 2: wait 2 → stale → backoff 8
        //   attempt 3: wait 2 → stale → exhausted (no backoff after final attempt)
        _delays.Recorded.Should().Equal(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SetTitle_PatchReturns500_LogsWarningPrefixedWithStreamTitleFailed_NoVerificationGet()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.InternalServerError);

        var client = CreateClient();

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
        result.ChannelsFailed.Should().Be(1);

        _logger.Invocations
            .Should()
            .Contain(i =>
                (LogLevel)i.Arguments[0] == LogLevel.Warning &&
                i.Arguments[2]!.ToString()!.StartsWith("StreamTitleFailed:"));

        _delays.Recorded.Should().BeEmpty("no InitialVerifyWait when PATCH itself failed");
    }

    [Fact]
    public async Task SetTitle_VerificationExhausted_ErrorLogIncludesForensicCsvFields()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);

        var patchCalls = 0;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Patch &&
                    r.RequestUri!.PathAndQuery.EndsWith("/user/channel-meta/ch1")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                patchCalls++;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty)
                };
                resp.Headers.TryAddWithoutValidation("cf-ray", $"patch-ray-{patchCalls}");
                resp.Headers.TryAddWithoutValidation("etag", $"W/\"patch-etag-{patchCalls}\"");
                return resp;
            });

        var getCalls = 0;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.EndsWith("/user/channel-meta/ch1")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                getCalls++;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { title = "STALE", description = (string?)null })
                };
                resp.Headers.TryAddWithoutValidation("cf-ray", $"get-ray-{getCalls}");
                resp.Headers.TryAddWithoutValidation("etag", $"W/\"get-etag-{getCalls}\"");
                return resp;
            });

        var client = CreateClient();
        await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        var errorLog = _logger.Invocations.Single(i =>
            (LogLevel)i.Arguments[0] == LogLevel.Error &&
            i.Arguments[2]!.ToString()!.StartsWith("StreamTitleFailed: RestreamVerificationExhausted"));

        var rendered = errorLog.Arguments[2]!.ToString()!;
        rendered.Should().Contain("patch_status_per_attempt=200,200,200");
        rendered.Should().Contain("get_status_per_attempt=200,200,200");
        rendered.Should().Contain("patch_cf_ray_per_attempt=patch-ray-1,patch-ray-2,patch-ray-3");
        rendered.Should().Contain("get_cf_ray_per_attempt=get-ray-1,get-ray-2,get-ray-3");
        rendered.Should().Contain("get_body_title_per_attempt=STALE,STALE,STALE");
        rendered.Should().Contain("patch-etag-1");
        rendered.Should().Contain("get-etag-3");
    }

    [Fact]
    public async Task SetTitle_TwoChannels_AVerifiesBExhausts_ReturnsUpdated1Failed1_LogsBOnly()
    {
        var channels = new[]
        {
            new { id = "chA", displayName = "YouTube",  enabled = true, streamingPlatformId = 5  },
            new { id = "chB", displayName = "Facebook", enabled = true, streamingPlatformId = 37 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("chA", HttpStatusCode.OK);
        SetupPatchChannel("chB", HttpStatusCode.OK);
        SetupVerifyGetChannel("chA", returnedTitle: "Friday Bible Study");
        SetupVerifyGetChannelSequence("chB", "STALE", "STALE", "STALE");

        var client = CreateClient();

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(1);

        var errorLogs = _logger.Invocations
            .Where(i => (LogLevel)i.Arguments[0] == LogLevel.Error &&
                        i.Arguments[2]!.ToString()!.StartsWith("StreamTitleFailed: RestreamVerificationExhausted"))
            .ToList();
        errorLogs.Should().HaveCount(1);
        errorLogs[0].Arguments[2]!.ToString().Should().Contain("chB");
        errorLogs[0].Arguments[2]!.ToString().Should().NotContain("chA");
    }
}
