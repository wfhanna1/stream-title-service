using FluentAssertions;
using Moq;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class YouTubeClientTests
{
    private readonly Mock<IYouTubeServiceWrapper> _youtubeService = new();

    // No-op delay so tests that exercise the broadcast-wait timeout don't actually sleep.
    private static readonly Func<TimeSpan, CancellationToken, Task> NoopDelay = (_, _) => Task.CompletedTask;

    // Short wait window so timeout-bound tests resolve quickly.
    private static readonly TimeSpan FastWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(5);

    private YouTubeClient NewFastClient() => new(
        _youtubeService.Object,
        logger: null,
        maxBroadcastWait: FastWait,
        broadcastPollInterval: FastInterval,
        delayAsync: NoopDelay);

    [Fact]
    public async Task SetTitle_WithActiveBroadcast_ShouldUpdateVideoTitle()
    {
        // Arrange: channel ID
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_my_channel");

        // Arrange: active broadcast matching our channel
        _youtubeService.Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("broadcast-video-123", "UC_my_channel", "Old Stream Title")
            });

        // Arrange: current video snippet
        var existingSnippet = new VideoSnippetInfo(
            "broadcast-video-123",
            "Old Stream Title",
            "Stream description",
            "UC_my_channel",
            new List<string> { "church", "liturgy" });

        _youtubeService.Setup(s => s.GetVideoSnippetAsync("broadcast-video-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSnippet);

        // Arrange: update succeeds
        _youtubeService.Setup(s => s.UpdateVideoSnippetAsync(
                "broadcast-video-123",
                It.IsAny<string>(),
                "Stream description",
                "UC_my_channel",
                It.Is<List<string>>(t => t.Contains("church")),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = new YouTubeClient(_youtubeService.Object);

        // Act
        var result = await client.SetTitleAsync("New Stream Title", CancellationToken.None);

        // Assert
        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(0);

        _youtubeService.Verify(s => s.UpdateVideoSnippetAsync(
            "broadcast-video-123",
            "New Stream Title",
            "Stream description",
            "UC_my_channel",
            It.Is<List<string>>(t => t.Contains("church") && t.Contains("liturgy")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetTitle_NoActiveBroadcast_ShouldReturnZero()
    {
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_my_channel");

        _youtubeService.Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>());

        // Fast wait/interval so the test resolves the timeout in milliseconds, not 30 seconds.
        var client = NewFastClient();

        var result = await client.SetTitleAsync("Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
        result.ChannelsFailed.Should().Be(0);

        // Should NOT attempt to get video snippet or update
        _youtubeService.Verify(s => s.GetVideoSnippetAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetTitle_BroadcastFromDifferentChannel_ShouldReturnZero()
    {
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_my_channel");

        _youtubeService.Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("other-video", "UC_other_channel", "Someone Else's Stream")
            });

        var client = NewFastClient();

        var result = await client.SetTitleAsync("Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
    }

    [Fact]
    public async Task SetTitle_ShouldPreserveExistingSnippetFields()
    {
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_ch");

        _youtubeService.Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("vid-1", "UC_ch", "Old Title")
            });

        _youtubeService.Setup(s => s.GetVideoSnippetAsync("vid-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoSnippetInfo(
                "vid-1", "Old Title", "My detailed description", "UC_ch",
                new List<string> { "coptic", "orthodox", "liturgy" }));

        _youtubeService.Setup(s => s.UpdateVideoSnippetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = new YouTubeClient(_youtubeService.Object);
        await client.SetTitleAsync("New Title", CancellationToken.None);

        // Verify the update preserved description and tags
        _youtubeService.Verify(s => s.UpdateVideoSnippetAsync(
            "vid-1",
            "New Title",
            "My detailed description",
            "UC_ch",
            It.Is<List<string>>(t =>
                t.Count == 3 &&
                t.Contains("coptic") &&
                t.Contains("orthodox") &&
                t.Contains("liturgy")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetTitle_GetChannelIdThrows_ShouldPropagate()
    {
        _youtubeService
            .Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Could not retrieve channel ID"));

        var client = new YouTubeClient(_youtubeService.Object);
        var act = () => client.SetTitleAsync("New Title", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*channel ID*");
    }

    [Fact]
    public async Task SetTitle_GetVideoSnippetThrows_ShouldPropagate()
    {
        _youtubeService
            .Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_ch");

        _youtubeService
            .Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("vid-1", "UC_ch", "Live Now")
            });

        _youtubeService
            .Setup(s => s.GetVideoSnippetAsync("vid-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Could not retrieve video details"));

        var client = new YouTubeClient(_youtubeService.Object);
        var act = () => client.SetTitleAsync("New Title", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*video details*");
    }

    [Fact]
    public async Task SetTitle_UpdateVideoThrows_ShouldPropagate()
    {
        _youtubeService
            .Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_ch");

        _youtubeService
            .Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("vid-1", "UC_ch", "Live Now")
            });

        _youtubeService
            .Setup(s => s.GetVideoSnippetAsync("vid-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoSnippetInfo("vid-1", "Live Now", "Description", "UC_ch", new List<string>()));

        _youtubeService
            .Setup(s => s.UpdateVideoSnippetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("YouTube API quota exceeded"));

        var client = new YouTubeClient(_youtubeService.Object);
        var act = () => client.SetTitleAsync("New Title", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*quota*");
    }

    [Fact]
    public async Task SetTitle_MultipleBroadcastsSameChannel_ShouldUpdateFirst()
    {
        _youtubeService
            .Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_ch");

        // Two broadcasts for the same channel; only the first should be updated
        _youtubeService
            .Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("vid-first", "UC_ch", "First Broadcast"),
                new("vid-second", "UC_ch", "Second Broadcast")
            });

        _youtubeService
            .Setup(s => s.GetVideoSnippetAsync("vid-first", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoSnippetInfo("vid-first", "First Broadcast", "Desc", "UC_ch", new List<string>()));

        _youtubeService
            .Setup(s => s.UpdateVideoSnippetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = new YouTubeClient(_youtubeService.Object);
        var result = await client.SetTitleAsync("New Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);

        // Only vid-first is updated; vid-second is never touched
        _youtubeService.Verify(s => s.UpdateVideoSnippetAsync(
            "vid-first",
            "New Title",
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _youtubeService.Verify(s => s.GetVideoSnippetAsync(
            "vid-second", It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------
    // Wait-with-retry for late-arriving YouTube broadcast
    //
    // YouTube's liveBroadcasts.list(broadcastStatus=active) does not
    // recognize a stream as "active" until its RTMP ingest registers
    // server-side, which can lag the OBS WebSocket "streaming started"
    // ack by 5-30 seconds. Without a wait, SetTitleAsync silently no-ops
    // and the title is never set on the visible broadcast.
    //
    // Real-world incident: 2026-04-29T09:25 UTC. OBS reported streaming
    // started at 09:25:40.363; our function queried YouTube at
    // 09:25:41.475 (~1.1s later); broadcast actually went live at
    // 09:25:46 (~5s after our query). Function returned (0,0) and
    // YouTube kept its channel-default title.
    // -----------------------------------------------------------------

    [Fact]
    public async Task SetTitle_BroadcastBecomesActiveAfterFirstPoll_ShouldRetryAndUpdate()
    {
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_ch");

        // First call: no broadcasts yet (broadcast hasn't gone active in YouTube).
        // Second call: broadcast is now active. Driven by SetupSequence so Moq
        // returns the next value on each successive call.
        _youtubeService.SetupSequence(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>())
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("vid-late", "UC_ch", "Late-arriving broadcast")
            });

        _youtubeService.Setup(s => s.GetVideoSnippetAsync("vid-late", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoSnippetInfo("vid-late", "Late-arriving broadcast", "Desc", "UC_ch", new List<string>()));

        _youtubeService.Setup(s => s.UpdateVideoSnippetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Fake delay that returns immediately so the test doesn't actually sleep
        // through the configured poll interval.
        Func<TimeSpan, CancellationToken, Task> noopDelay = (_, _) => Task.CompletedTask;

        var client = new YouTubeClient(
            _youtubeService.Object,
            logger: null,
            maxBroadcastWait: TimeSpan.FromSeconds(30),
            broadcastPollInterval: TimeSpan.FromSeconds(2),
            delayAsync: noopDelay);

        var result = await client.SetTitleAsync("New Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(0);

        _youtubeService.Verify(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task SetTitle_SnippetWithNullTags_ShouldHandleGracefully()
    {
        _youtubeService
            .Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_ch");

        _youtubeService
            .Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("vid-1", "UC_ch", "Live Now")
            });

        // Simulate a wrapper that returns null tags (e.g. video has no tags set)
        _youtubeService
            .Setup(s => s.GetVideoSnippetAsync("vid-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoSnippetInfo("vid-1", "Live Now", "Description", "UC_ch", null!));

        _youtubeService
            .Setup(s => s.UpdateVideoSnippetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = new YouTubeClient(_youtubeService.Object);
        var result = await client.SetTitleAsync("New Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);

        // Update should be called with null tags passed through unchanged
        _youtubeService.Verify(s => s.UpdateVideoSnippetAsync(
            "vid-1",
            "New Title",
            "Description",
            "UC_ch",
            null!,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
