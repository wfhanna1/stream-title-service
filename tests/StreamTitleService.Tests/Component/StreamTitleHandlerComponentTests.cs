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
    //             full formatted title, success logged (not published).
    // ------------------------------------------------------------------
    [Fact]
    public async Task HappyPath_ExplicitTitle_FormatsAndSetsTitle()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("virtual", "Arabic Bible Study", timestamp);

        _restreamFake.ResultToReturn = new TitleUpdateResult(3, 0);

        await _handler.HandleAsync(evt, CancellationToken.None);

        // The platform client received exactly one call with the full formatted title.
        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        sentTitle.Should().Contain("Arabic Bible Study");
        sentTitle.Should().MatchRegex(@"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\s+\w+\s+\d{1,2},\s+\d{4}\s+-\s+Arabic Bible Study$");

        // No alerts.
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
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("virtual", null, timestamp);

        _restreamFake.ResultToReturn = new TitleUpdateResult(2, 0);

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        sentTitle.Should().Contain("Divine Liturgy");
        sentTitle.Should().MatchRegex(@"^Sunday,\s+March\s+29,\s+2026\s+-\s+Divine Liturgy$");

        _alertNotifier.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 3: RestreamClient throws -> failure logged, alert sent,
    //             exception propagated.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FailurePath_ClientThrows_LogsFailureAndAlert()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("virtual", "Test Stream", timestamp);

        _restreamFake.ExceptionToThrow = new InvalidOperationException("Token refresh failed");

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Token refresh failed");

        // An alert was sent.
        _alertNotifier.Alerts.Should().ContainSingle();
        _alertNotifier.Alerts[0].Error.Should().Contain("Token refresh failed");
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
        _alertNotifier.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 5: No title, Saturday 7 PM EDT -> Vespers and Midnight Praises.
    // 2026-03-28 is a Saturday. 23:00 UTC = 19:00 EDT (UTC-4).
    // ------------------------------------------------------------------
    [Fact]
    public async Task DefaultTitle_SaturdayEvening_ProducesVespersTitle()
    {
        // Far-future Saturday 7 PM EDT (UTC-4) to avoid staleness check
        // Saturday April 4, 2026 19:00 EDT = 23:00 UTC
        var timestamp = new DateTimeOffset(2026, 4, 4, 23, 0, 0, TimeSpan.Zero);
        var evt = CreateEvent("virtual", null, timestamp);

        _restreamFake.ResultToReturn = new TitleUpdateResult(2, 0);

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        sentTitle.Should().Be("Saturday, April 04, 2026 - Vespers and Midnight Praises");

        _youtubeFake.TitlesReceived.Should().BeEmpty();
        _alertNotifier.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 6: location "st. anthony chapel" routes to YouTube client.
    // ------------------------------------------------------------------
    [Fact]
    public async Task YouTubePath_StAnthonyChapel_RoutesToYouTubeClient()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("st. anthony chapel", "Sunday Liturgy", timestamp);

        _youtubeFake.ResultToReturn = new TitleUpdateResult(1, 0);

        await _handler.HandleAsync(evt, CancellationToken.None);

        _youtubeFake.TitlesReceived.Should().ContainSingle();
        _youtubeFake.TitlesReceived[0].Should().Contain("Sunday Liturgy");

        // Restream client must not have been called.
        _restreamFake.TitlesReceived.Should().BeEmpty();

        _alertNotifier.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 7: Unknown location "holy cross" -> throws, logs failure,
    //             sends alert.
    // ------------------------------------------------------------------
    [Fact]
    public async Task UnknownLocation_ThroughFullPipeline_LogsFailureAndAlerts()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("holy cross", "Some Title", timestamp);

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();

        _alertNotifier.Alerts.Should().ContainSingle();

        // No platform client calls.
        _restreamFake.TitlesReceived.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 8: Title already has a date prefix for a different date.
    //             The old prefix must be stripped and replaced with the
    //             date derived from the event timestamp (March 29, 2026).
    // ------------------------------------------------------------------
    [Fact]
    public async Task DatePrefixStripping_ExistingDateInTitle()
    {
        // 2026-03-29 13:00 UTC = Sunday, March 29, 2026 09:00 Eastern.
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("virtual", "Sunday, March 22, 2026 - Old Liturgy", timestamp);

        _restreamFake.ResultToReturn = new TitleUpdateResult(1, 0);

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        // Old date prefix stripped; new prefix from event timestamp applied.
        sentTitle.Should().Be("Sunday, March 29, 2026 - Old Liturgy");

        _alertNotifier.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 9: Platform client returns partial failure (2 updated,
    //             1 failed). Handler completes successfully (no throw).
    // ------------------------------------------------------------------
    [Fact]
    public async Task PartialPlatformFailure_StillCompletes()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var evt = CreateEvent("virtual", "Partial Update Stream", timestamp);

        _restreamFake.ResultToReturn = new TitleUpdateResult(2, 1);

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamFake.TitlesReceived.Should().ContainSingle();
        _alertNotifier.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Scenario 10: Two back-to-back events with different titles both
    //              processed successfully. Verifies handler has no stale
    //              state between calls.
    // ------------------------------------------------------------------
    [Fact]
    public async Task BackToBackEvents_BothProcessedSuccessfully()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var evt1 = CreateEvent("virtual", "First Stream Title", timestamp);
        var evt2 = CreateEvent("virtual", "Second Stream Title", timestamp);

        _restreamFake.ResultToReturn = new TitleUpdateResult(2, 0);

        await _handler.HandleAsync(evt1, CancellationToken.None);
        await _handler.HandleAsync(evt2, CancellationToken.None);

        _restreamFake.TitlesReceived.Should().HaveCount(2);
        _restreamFake.TitlesReceived[0].Should().Contain("First Stream Title");
        _restreamFake.TitlesReceived[1].Should().Contain("Second Stream Title");

        _alertNotifier.Alerts.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
    }
}
