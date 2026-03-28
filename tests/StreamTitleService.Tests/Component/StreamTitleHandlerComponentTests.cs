using FluentAssertions;
using StreamTitleService.Application;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Configuration;
using Xunit;

namespace StreamTitleService.Tests.Component;

// ---------------------------------------------------------------------------
// Fake adapters
// ---------------------------------------------------------------------------

internal sealed class FakeTitlePlatformClient : ITitlePlatformClient
{
    public List<string> TitlesReceived { get; } = new();
    public TitleUpdateResult ResultToReturn { get; set; } = new(1, 0);
    public Exception? ExceptionToThrow { get; set; }

    public Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct)
    {
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        TitlesReceived.Add(title);
        return Task.FromResult(ResultToReturn);
    }
}

internal sealed class FakeEventPublisher : IEventPublisher
{
    public List<StreamTitleSetEvent> TitleSetEvents { get; } = new();
    public List<StreamTitleFailedEvent> TitleFailedEvents { get; } = new();

    public Task PublishTitleSetAsync(StreamTitleSetEvent evt, CancellationToken ct)
    {
        TitleSetEvents.Add(evt);
        return Task.CompletedTask;
    }

    public Task PublishTitleFailedAsync(StreamTitleFailedEvent evt, CancellationToken ct)
    {
        TitleFailedEvents.Add(evt);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAlertNotifier : IAlertNotifier
{
    public List<(string Title, string Error)> Alerts { get; } = new();

    public Task SendFailureAlertAsync(string title, string error, CancellationToken ct)
    {
        Alerts.Add((title, error));
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// Component tests
// ---------------------------------------------------------------------------

[Trait("Category", "Component")]
public class StreamTitleHandlerComponentTests
{
    private readonly FakeTitlePlatformClient _restreamFake = new();
    private readonly FakeTitlePlatformClient _youtubeFake = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly FakeAlertNotifier _alertNotifier = new();
    private readonly StreamTitleHandler _handler;

    public StreamTitleHandlerComponentTests()
    {
        var clients = new Dictionary<TargetPlatform, ITitlePlatformClient>
        {
            [TargetPlatform.Restream] = _restreamFake,
            [TargetPlatform.YouTube] = _youtubeFake
        };

        _handler = new StreamTitleHandler(
            new LocationPlatformMapping(),
            clients,
            _eventPublisher,
            _alertNotifier,
            stalenessThresholdSeconds: 90);
    }

    private static StreamStartedEvent CreateEvent(string location, string? title, DateTimeOffset timestamp)
    {
        return new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "component-test",
            Timestamp = timestamp,
            Location = location,
            Data = new StreamStartedData { Title = title }
        };
    }

    // ------------------------------------------------------------------
    // Scenario 1: StreamStarted with explicit title "Arabic Bible Study"
    //             for location "virtual" -> RestreamClient called with
    //             full formatted title, StreamTitleSetEvent published.
    // ------------------------------------------------------------------
    [Fact]
    public async Task HappyPath_ExplicitTitle_FormatsAndSetsTitle()
    {
        // Use a fresh timestamp so the staleness check does not drop the event.
        // We only care about the date prefix; the regex assertion covers it generically.
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("virtual", "Arabic Bible Study", timestamp);

        _restreamFake.ResultToReturn = new TitleUpdateResult(3, 0);

        await _handler.HandleAsync(evt, CancellationToken.None);

        // The platform client received exactly one call with the full formatted title.
        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        sentTitle.Should().Contain("Arabic Bible Study");
        sentTitle.Should().MatchRegex(@"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\s+\w+\s+\d{1,2},\s+\d{4}\s+-\s+Arabic Bible Study$");

        // A StreamTitleSetEvent was published with the correct data.
        _eventPublisher.TitleSetEvents.Should().ContainSingle();
        var published = _eventPublisher.TitleSetEvents[0];
        published.Data.Title.Should().Be(sentTitle);
        published.Data.TargetPlatform.Should().Be("restream");
        published.Data.ChannelsUpdated.Should().Be(3);
        published.Location.Should().Be("virtual");

        // No failure events, no alerts.
        _eventPublisher.TitleFailedEvents.Should().BeEmpty();
        _alertNotifier.Alerts.Should().BeEmpty();

        // YouTube client was never touched.
        _youtubeFake.TitlesReceived.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 2: No title on Sunday morning -> DefaultTitleGenerator
    //             produces "Divine Liturgy", full formatted title set.
    // ------------------------------------------------------------------
    [Fact]
    public async Task HappyPath_NoTitle_SundayMorning_ProducesDefaultTitle()
    {
        // 2026-03-29 13:00 UTC is Sunday, March 29, 2026 09:00 Eastern.
        var timestamp = new DateTimeOffset(2026, 3, 29, 13, 0, 0, TimeSpan.Zero);
        var evt = CreateEvent("virtual", null, timestamp);

        _restreamFake.ResultToReturn = new TitleUpdateResult(2, 0);

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        sentTitle.Should().Contain("Divine Liturgy");
        sentTitle.Should().MatchRegex(@"^Sunday,\s+March\s+29,\s+2026\s+-\s+Divine Liturgy$");

        _eventPublisher.TitleSetEvents.Should().ContainSingle();
        var published = _eventPublisher.TitleSetEvents[0];
        published.Data.Title.Should().Be(sentTitle);
        published.Data.TargetPlatform.Should().Be("restream");

        _eventPublisher.TitleFailedEvents.Should().BeEmpty();
        _alertNotifier.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 3: RestreamClient throws -> StreamTitleFailedEvent
    //             published, alert sent, exception propagated.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FailurePath_ClientThrows_PublishesFailedEventAndAlert()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("virtual", "Test Stream", timestamp);

        _restreamFake.ExceptionToThrow = new InvalidOperationException("Token refresh failed");

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Token refresh failed");

        // A failure event was published.
        _eventPublisher.TitleFailedEvents.Should().ContainSingle();
        var failedEvt = _eventPublisher.TitleFailedEvents[0];
        failedEvt.Data.Error.Should().Contain("Token refresh failed");
        failedEvt.Data.TargetPlatform.Should().Be("restream");

        // An alert was sent.
        _alertNotifier.Alerts.Should().ContainSingle();
        _alertNotifier.Alerts[0].Error.Should().Contain("Token refresh failed");

        // No success events.
        _eventPublisher.TitleSetEvents.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 4: Stale event skipped entirely, no fakes called.
    // ------------------------------------------------------------------
    [Fact]
    public async Task StalenessCheck_OldEvent_SkippedWithoutCallingAnything()
    {
        // 120 seconds old; threshold is 90 seconds.
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-120);
        var evt = CreateEvent("virtual", "Old Title", timestamp);

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamFake.TitlesReceived.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
        _eventPublisher.TitleSetEvents.Should().BeEmpty();
        _eventPublisher.TitleFailedEvents.Should().BeEmpty();
        _alertNotifier.Alerts.Should().BeEmpty();
    }
}
