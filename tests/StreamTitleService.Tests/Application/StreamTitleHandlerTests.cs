using FluentAssertions;
using Moq;
using StreamTitleService.Application;
using Xunit;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.Exceptions;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Configuration;

namespace StreamTitleService.Tests.Application;

public class StreamTitleHandlerTests
{
    private readonly Mock<ITitlePlatformClient> _restreamClient = new();
    private readonly Mock<ITitlePlatformClient> _youtubeClient = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly Mock<IAlertNotifier> _alertNotifier = new();
    private readonly StreamTitleHandler _handler;

    public StreamTitleHandlerTests()
    {
        var clients = new Dictionary<TargetPlatform, ITitlePlatformClient>
        {
            [TargetPlatform.Restream] = _restreamClient.Object,
            [TargetPlatform.YouTube] = _youtubeClient.Object
        };

        _handler = new StreamTitleHandler(
            new LocationPlatformMapping(),
            clients,
            _eventPublisher.Object,
            _alertNotifier.Object,
            stalenessThresholdSeconds: 90);
    }

    [Fact]
    public async Task Handle_ValidEvent_ShouldSetTitleAndPublishSuccess()
    {
        var evt = CreateEvent("virtual", "Arabic Bible Study",
            DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(3, 0));

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamClient.Verify(c => c.SetTitleAsync(
            It.Is<string>(t => t.Contains("Arabic Bible Study")),
            It.IsAny<CancellationToken>()), Times.Once);
        _eventPublisher.Verify(p => p.PublishTitleSetAsync(
            It.Is<StreamTitleSetEvent>(e => e.Data.TargetPlatform == "restream"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PlatformClientFails_ShouldPublishFailedAndAlert()
    {
        var evt = CreateEvent("virtual", "Test", DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Token refresh failed"));

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        _eventPublisher.Verify(p => p.PublishTitleFailedAsync(
            It.IsAny<StreamTitleFailedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _alertNotifier.Verify(a => a.SendFailureAlertAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_StaleEvent_ShouldSkipWithoutProcessing()
    {
        var evt = CreateEvent("virtual", "Old Title",
            DateTimeOffset.UtcNow.AddSeconds(-120)); // 120s old, threshold is 90s

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamClient.Verify(c => c.SetTitleAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventPublisher.Verify(p => p.PublishTitleSetAsync(
            It.IsAny<StreamTitleSetEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_UnknownLocation_ShouldPublishFailedAndAlert()
    {
        var evt = CreateEvent("unknown-place", null, DateTimeOffset.UtcNow);

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<UnknownLocationException>();
        _eventPublisher.Verify(p => p.PublishTitleFailedAsync(
            It.IsAny<StreamTitleFailedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _alertNotifier.Verify(a => a.SendFailureAlertAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AtExactStalenessThreshold_ShouldProcess()
    {
        // 89s old -- the code uses > (not >=), so anything at or below the threshold should be processed.
        // Using 89s avoids a race condition where execution time tips a 90s timestamp over the boundary.
        var evt = CreateEvent("virtual", "Test Title", DateTimeOffset.UtcNow.AddSeconds(-89));
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(1, 0));

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamClient.Verify(c => c.SetTitleAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PartialSuccess_ShouldPublishWithCorrectCounts()
    {
        var evt = CreateEvent("virtual", "Arabic Bible Study", DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(2, 1));

        await _handler.HandleAsync(evt, CancellationToken.None);

        _eventPublisher.Verify(p => p.PublishTitleSetAsync(
            It.Is<StreamTitleSetEvent>(e =>
                e.Data.ChannelsUpdated == 2 && e.Data.ChannelsFailed == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PublishSuccessEventFails_ShouldStillSucceed()
    {
        var evt = CreateEvent("virtual", "Arabic Bible Study", DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(1, 0));
        _eventPublisher.Setup(p => p.PublishTitleSetAsync(It.IsAny<StreamTitleSetEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Service Bus unavailable"));

        // Should not throw -- the title was set, publish failure is non-fatal
        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Handle_AlertNotifierFailsOnUnknownLocation_ShouldStillThrowOriginalException()
    {
        var evt = CreateEvent("unknown-place", null, DateTimeOffset.UtcNow);
        _alertNotifier.Setup(a => a.SendFailureAlertAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Alert service down"));

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<UnknownLocationException>();
    }

    [Fact]
    public async Task Handle_StAnthony_ShouldRouteToYouTube()
    {
        var evt = CreateEvent("st. anthony chapel", "Test",
            DateTimeOffset.UtcNow);
        _youtubeClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(1, 0));

        await _handler.HandleAsync(evt, CancellationToken.None);

        _youtubeClient.Verify(c => c.SetTitleAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _restreamClient.Verify(c => c.SetTitleAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static StreamStartedEvent CreateEvent(string location, string? title, DateTimeOffset timestamp)
    {
        return new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "test",
            Timestamp = timestamp,
            Location = location,
            Data = new StreamStartedData { Title = title }
        };
    }
}
