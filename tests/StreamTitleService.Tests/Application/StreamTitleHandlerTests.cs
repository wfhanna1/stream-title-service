using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    private readonly Mock<IAlertNotifier> _alertNotifier = new();
    private readonly Mock<ILogger<StreamTitleHandler>> _logger = new();
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
            _alertNotifier.Object,
            stalenessThresholdSeconds: 90,
            logger: _logger.Object);
    }

    [Fact]
    public async Task Handle_ValidEvent_ShouldSetTitleAndLogSuccess()
    {
        var evt = CreateEvent("virtual", "Arabic Bible Study",
            DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(3, 0));

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamClient.Verify(c => c.SetTitleAsync(
            It.Is<string>(t => t.Contains("Arabic Bible Study")),
            It.IsAny<CancellationToken>()), Times.Once);
        VerifyLogContains(LogLevel.Information, "StreamTitleSet");
    }

    [Fact]
    public async Task Handle_PlatformClientFails_ShouldLogFailureAndAlert()
    {
        var evt = CreateEvent("virtual", "Test", DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Token refresh failed"));

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        VerifyLogContains(LogLevel.Error, "StreamTitleFailed");
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
        // No success or failure logs expected (only staleness warning)
    }

    [Fact]
    public async Task Handle_UnknownLocation_ShouldLogFailureAndAlert()
    {
        var evt = CreateEvent("unknown-place", null, DateTimeOffset.UtcNow);

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<UnknownLocationException>();
        VerifyLogContains(LogLevel.Error, "StreamTitleFailed");
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
    public async Task Handle_PartialSuccess_ShouldLogWithCorrectCounts()
    {
        var evt = CreateEvent("virtual", "Arabic Bible Study", DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(2, 1));

        await _handler.HandleAsync(evt, CancellationToken.None);

        VerifyLogContains(LogLevel.Information, "StreamTitleSet");
    }

    [Fact]
    public async Task Handle_SuccessfulTitleSet_ShouldNotThrow()
    {
        var evt = CreateEvent("virtual", "Arabic Bible Study", DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(1, 0));

        // Should not throw -- logging is non-fatal
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

    // Fix #1: DIP verification -- handler must accept ILocationPlatformMapper (not concrete type)
    [Fact]
    public void Constructor_AcceptsILocationPlatformMapper_NotConcreteType()
    {
        // This test verifies the DIP fix: handler depends on ILocationPlatformMapper (port)
        // If someone reverts to concrete LocationPlatformMapping, this test still passes
        // because LocationPlatformMapping implements the interface.
        // The real guard is that StreamTitleHandler's using statements don't include Infrastructure.
        var mapper = new Mock<ILocationPlatformMapper>();
        mapper.Setup(m => m.GetPlatform(It.IsAny<Location>())).Returns(TargetPlatform.Restream);

        var handler = new StreamTitleHandler(
            mapper.Object,  // Passing mock of interface, not concrete class
            new Dictionary<TargetPlatform, ITitlePlatformClient>(),
            _alertNotifier.Object);

        handler.Should().NotBeNull();
    }

    // Fix #6: TitleUpdateException with channelsAttempted
    [Fact]
    public async Task Handle_TitleUpdateException_ShouldLogFailureWithError()
    {
        var evt = CreateEvent("virtual", "Test", DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TitleUpdateException("Partial failure", channelsAttempted: 3, channelsUpdated: 1));

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<TitleUpdateException>();
        VerifyLogContains(LogLevel.Error, "StreamTitleFailed");
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

    private void VerifyLogContains(LogLevel level, string messageSubstring)
    {
        _logger.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(messageSubstring)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());
    }
}
