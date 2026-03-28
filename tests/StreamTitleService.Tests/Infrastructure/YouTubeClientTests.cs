using FluentAssertions;
using Moq;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class YouTubeClientTests
{
    private readonly Mock<IYouTubeServiceWrapper> _youtubeService = new();

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

        var client = new YouTubeClient(_youtubeService.Object);

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

        var client = new YouTubeClient(_youtubeService.Object);

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
}
