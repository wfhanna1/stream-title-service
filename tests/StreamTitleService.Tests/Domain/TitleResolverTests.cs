using FluentAssertions;
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.Services;
using StreamTitleService.Domain.ValueObjects;
using Xunit;

namespace StreamTitleService.Tests.Domain;

public class TitleResolverTests
{
    private readonly TitleResolver _resolver = new();

    [Fact]
    public void Resolve_WithExplicitTitle_ShouldUseIt()
    {
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "zoom-automation",
            Timestamp = new DateTimeOffset(2026, 3, 27, 19, 5, 0, TimeSpan.Zero),
            Location = "virtual",
            Data = new StreamStartedData { Title = "Arabic Bible Study" }
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Friday, March 27, 2026 - Arabic Bible Study");
    }

    [Fact]
    public void Resolve_WithNoTitle_SundayMorning_ShouldReturnDivineLiturgy()
    {
        // Sunday 10 AM Eastern = Sunday 15:00 UTC (during EDT, March)
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 29, 15, 0, 0, TimeSpan.Zero),
            Location = "st. mary and st. joseph",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Sunday, March 29, 2026 - Divine Liturgy");
    }

    [Fact]
    public void Resolve_WithNoTitle_SaturdayEvening_ShouldReturnVespers()
    {
        // Saturday March 28 2026: EDT is active (DST starts March 8)
        // Saturday 7 PM EDT = 23:00 UTC Saturday
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero),
            Location = "st. anthony chapel",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Saturday, March 28, 2026 - Vespers and Midnight Praises");
    }

    [Fact]
    public void Resolve_WithNoTitle_SaturdayAt1159PM_ShouldReturnVespers()
    {
        // Saturday 11:59 PM Eastern
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "test",
            Timestamp = new DateTimeOffset(2026, 3, 29, 3, 59, 0, TimeSpan.Zero), // 11:59 PM EDT Sat
            Location = "virtual",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Contain("Vespers and Midnight Praises");
    }

    [Fact]
    public void Resolve_WithNoTitle_SaturdayAtMidnight_ShouldReturnDivineLiturgy()
    {
        // Sunday 12:00 AM Eastern (no longer Saturday)
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "test",
            Timestamp = new DateTimeOffset(2026, 3, 29, 4, 0, 0, TimeSpan.Zero), // 12:00 AM EDT Sun
            Location = "virtual",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Contain("Divine Liturgy");
    }

    [Fact]
    public void Resolve_WithExplicitTitle_OnSaturdayEvening_ShouldOverrideDefault()
    {
        // Saturday 7 PM but with explicit title
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero),
            Location = "virtual",
            Data = new StreamStartedData { Title = "Feast of St. Mark" }
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Saturday, March 28, 2026 - Feast of St. Mark");
    }

    [Fact]
    public void Resolve_WithEmptyStringTitle_ShouldUseDefault()
    {
        // Sunday morning -- empty title should trigger default "Divine Liturgy"
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 29, 15, 0, 0, TimeSpan.Zero), // Sunday 11 AM EDT
            Location = "st. mary and st. joseph",
            Data = new StreamStartedData { Title = "" }
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Sunday, March 29, 2026 - Divine Liturgy");
    }

    [Fact]
    public void Resolve_WithWhitespaceOnlyTitle_ShouldUseDefault()
    {
        // Sunday morning -- whitespace-only title should trigger default "Divine Liturgy"
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 29, 15, 0, 0, TimeSpan.Zero), // Sunday 11 AM EDT
            Location = "st. mary and st. joseph",
            Data = new StreamStartedData { Title = "   " }
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Sunday, March 29, 2026 - Divine Liturgy");
    }

    [Fact]
    public void Resolve_Saturday459PM_ShouldReturnDivineLiturgy()
    {
        // Saturday 4:59 PM EDT = 20:59 UTC -- just before the 5 PM Vespers cutoff
        // eastern.Hour == 16, which is < 17, so returns "Divine Liturgy"
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 28, 20, 59, 0, TimeSpan.Zero),
            Location = "virtual",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Contain("Divine Liturgy");
    }

    [Fact]
    public void Resolve_Saturday500PM_ShouldReturnVespers()
    {
        // Saturday 5:00 PM EDT = 21:00 UTC -- exactly at the Vespers cutoff
        // eastern.Hour == 17, which is >= 17, so returns "Vespers and Midnight Praises"
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 28, 21, 0, 0, TimeSpan.Zero),
            Location = "virtual",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Contain("Vespers and Midnight Praises");
    }
}
