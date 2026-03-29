using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using StreamTitleService.Application;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Functions;
using StreamTitleService.Infrastructure.Configuration;
using Xunit;

namespace StreamTitleService.Tests.Isolated;

// ---------------------------------------------------------------------------
// Fake adapters (isolated from external services)
// ---------------------------------------------------------------------------

internal sealed class FakeTokenProvider : ITokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken ct)
        => Task.FromResult("fake-hardcoded-token");
}

internal sealed class FakeIsolatedTitlePlatformClient : ITitlePlatformClient
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

internal sealed class FakeIsolatedAlertNotifier : IAlertNotifier
{
    public List<(string Title, string Error)> Alerts { get; } = new();

    public Task SendFailureAlertAsync(string title, string error, CancellationToken ct)
    {
        Alerts.Add((title, error));
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// Isolated tests
// ---------------------------------------------------------------------------

[Trait("Category", "Isolated")]
public class IsolatedFunctionTests
{
    private readonly FakeIsolatedTitlePlatformClient _restreamFake = new();
    private readonly FakeIsolatedTitlePlatformClient _youtubeFake = new();
    private readonly FakeIsolatedAlertNotifier _alertFake = new();
    private readonly StreamTitleFunction _function;

    public IsolatedFunctionTests()
    {
        var mapping = new LocationPlatformMapping();

        var clients = new Dictionary<TargetPlatform, ITitlePlatformClient>
        {
            [TargetPlatform.Restream] = _restreamFake,
            [TargetPlatform.YouTube] = _youtubeFake
        };

        var handler = new StreamTitleHandler(mapping, clients, _alertFake);
        var logger = new Mock<ILogger<StreamTitleFunction>>();
        _function = new StreamTitleFunction(handler, logger.Object);
    }

    // ------------------------------------------------------------------
    // Test 1: Full pipeline - Restream path from raw JSON
    // Location "virtual" maps to Restream. Use a fresh timestamp so the
    // staleness check passes; verify the formatted title was set.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_RestreamPath_FromRawJson()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var json = $$"""
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "{{timestamp:o}}",
                "location": "virtual",
                "data": { "title": "Arabic Bible Study" }
            }
            """;

        await _function.Run(json);

        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        sentTitle.Should().Contain("Arabic Bible Study");
        sentTitle.Should().MatchRegex(@"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\s+\w+\s+\d{1,2},\s+\d{4}\s+-\s+Arabic Bible Study$");

        _youtubeFake.TitlesReceived.Should().BeEmpty();
        _alertFake.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test 2: Full pipeline - YouTube path from raw JSON
    // Location "st. anthony chapel" maps to YouTube. Use a fresh timestamp.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_YouTubePath_FromRawJson()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var json = $$"""
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "{{timestamp:o}}",
                "location": "st. anthony chapel",
                "data": { "title": "Test" }
            }
            """;

        await _function.Run(json);

        _youtubeFake.TitlesReceived.Should().ContainSingle();
        _youtubeFake.TitlesReceived[0].Should().Contain("Test");

        _restreamFake.TitlesReceived.Should().BeEmpty();
        _alertFake.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test 3: Full pipeline - default title on Sunday morning
    // No title in data; Sunday morning timestamp -> "Divine Liturgy".
    // 2026-03-29 13:00 UTC = Sunday 09:00 Eastern.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_DefaultTitle_FromRawJson()
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var json = $$"""
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "{{now}}",
                "location": "virtual",
                "data": {}
            }
            """;

        await _function.Run(json);

        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        sentTitle.Should().Contain("Divine Liturgy");

        _alertFake.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test 4: Schema version "2" rejected before handler is invoked.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_SchemaVersionRejected_FromRawJson()
    {
        var json = """
            {
                "schemaVersion": "2",
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "2026-03-27T14:00:00Z",
                "location": "virtual",
                "data": { "title": "Some Title" }
            }
            """;

        var act = () => _function.Run(json);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unrecognized schema version*");

        _restreamFake.TitlesReceived.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test 5: Event type "StreamTitleSet" is filtered out; handler NOT called.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_EventTypeFiltered_FromRawJson()
    {
        var json = """
            {
                "eventType": "StreamTitleSet",
                "source": "stream-title-service",
                "timestamp": "2026-03-27T14:00:00Z",
                "location": "virtual",
                "data": { "title": "Some Title" }
            }
            """;

        await _function.Run(json);

        // Function returns early without calling handler, so no platform calls.
        _restreamFake.TitlesReceived.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test 6: Missing/default timestamp throws ArgumentException.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_InvalidTimestamp_FromRawJson()
    {
        var json = """
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "location": "virtual",
                "data": {}
            }
            """;

        var act = () => _function.Run(json);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Timestamp*");

        _restreamFake.TitlesReceived.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test: Two consecutive events with different titles both processed.
    // Verifies the full function + handler pipeline handles sequential
    // messages without stale state.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_TwoConsecutiveEvents_BothProcessed()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var json1 = $$"""
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "{{timestamp:o}}",
                "location": "virtual",
                "data": { "title": "Event 1" }
            }
            """;

        var json2 = $$"""
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "{{timestamp:o}}",
                "location": "virtual",
                "data": { "title": "Event 2" }
            }
            """;

        await _function.Run(json1);
        await _function.Run(json2);

        _restreamFake.TitlesReceived.Should().HaveCount(2);
        _restreamFake.TitlesReceived[0].Should().Contain("Event 1");
        _restreamFake.TitlesReceived[1].Should().Contain("Event 2");

        _alertFake.Alerts.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test: data: {} with no title triggers DefaultTitleGenerator.
    // Use a Sunday morning timestamp so the default is "Divine Liturgy".
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_EventWithNoData_UsesDefaultTitle()
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var json = $$"""
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "{{now}}",
                "location": "virtual",
                "data": {}
            }
            """;

        await _function.Run(json);

        _restreamFake.TitlesReceived.Should().ContainSingle();
        var sentTitle = _restreamFake.TitlesReceived[0];
        sentTitle.Should().Contain("Divine Liturgy");

        _alertFake.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test 7: Stale event (2 minutes old) - no platform client calls.
    // The staleness threshold defaults to 90 seconds.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_StaleEvent_FromRawJson()
    {
        var staleTimestamp = DateTimeOffset.UtcNow.AddSeconds(-120).ToString("o");
        var json = $$$"""
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "{{{staleTimestamp}}}",
                "location": "virtual",
                "data": { "title": "Old Title" }
            }
            """;

        await _function.Run(json);

        _restreamFake.TitlesReceived.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
        _alertFake.Alerts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Test 8: Unknown location throws, alert sent.
    // Use a fresh timestamp so the staleness check does not short-circuit.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FullPipeline_UnknownLocation_FromRawJson()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var json = $$"""
            {
                "eventType": "StreamStarted",
                "source": "isolated-test",
                "timestamp": "{{timestamp:o}}",
                "location": "unknown-place",
                "data": { "title": "Some Title" }
            }
            """;

        var act = () => _function.Run(json);
        await act.Should().ThrowAsync<Exception>();

        _alertFake.Alerts.Should().ContainSingle();

        _restreamFake.TitlesReceived.Should().BeEmpty();
        _youtubeFake.TitlesReceived.Should().BeEmpty();
    }
}
