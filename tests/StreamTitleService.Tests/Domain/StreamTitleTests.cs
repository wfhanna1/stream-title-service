using FluentAssertions;
using StreamTitleService.Domain.ValueObjects;
using Xunit;

namespace StreamTitleService.Tests.Domain;

public class StreamTitleTests
{
    [Fact]
    public void Format_WithSuffix_ShouldPrependDate()
    {
        var timestamp = new DateTimeOffset(2026, 3, 29, 14, 0, 0, TimeSpan.Zero); // Sunday UTC
        var title = StreamTitle.Format("Divine Liturgy", timestamp);
        title.Value.Should().Be("Sunday, March 29, 2026 - Divine Liturgy");
    }

    [Fact]
    public void Format_ShouldUseEasternTimezone()
    {
        // 2026-03-28 03:00 UTC = 2026-03-27 11:00 PM EST (Friday)
        var timestamp = new DateTimeOffset(2026, 3, 28, 3, 0, 0, TimeSpan.Zero);
        var title = StreamTitle.Format("Test", timestamp);
        title.Value.Should().StartWith("Friday, March 27, 2026");
    }

    [Fact]
    public void Format_WithDatePrefix_ShouldStripAndReformat()
    {
        var timestamp = new DateTimeOffset(2026, 3, 29, 14, 0, 0, TimeSpan.Zero);
        var title = StreamTitle.Format("Sunday, March 29, 2026 - Divine Liturgy", timestamp);
        title.Value.Should().Be("Sunday, March 29, 2026 - Divine Liturgy");
        // Should NOT be "Sunday, March 29, 2026 - Sunday, March 29, 2026 - Divine Liturgy"
    }

    [Fact]
    public void Format_WithEmptySuffix_ShouldThrow()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var act = () => StreamTitle.Format("", timestamp);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Format_WithWhitespaceOnlySuffix_ShouldThrow()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var act = () => StreamTitle.Format("   ", timestamp);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Format_WithSpecialCharacters_ShouldPreserve()
    {
        var timestamp = new DateTimeOffset(2026, 3, 29, 14, 0, 0, TimeSpan.Zero); // Sunday UTC
        var suffix = "Q&A: \"Ask the Priest\" & More";
        var title = StreamTitle.Format(suffix, timestamp);
        title.Value.Should().Be($"Sunday, March 29, 2026 - {suffix}");
    }

    [Fact]
    public void Format_DuringDSTTransition_ShouldHandleCorrectly()
    {
        // March 8, 2026: DST springs forward at 2 AM EST -> 3 AM EDT
        // 2:30 AM local time does not exist. UTC 07:30 on March 8 maps to 3:30 AM EDT.
        var timestamp = new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero); // 3:30 AM EDT
        var title = StreamTitle.Format("Divine Liturgy", timestamp);
        title.Value.Should().Be("Sunday, March 08, 2026 - Divine Liturgy");
    }

    [Fact]
    public void Format_WithDifferentDateInPrefix_ShouldStripAndUseEventTimestamp()
    {
        // Suffix already has March 22 date prefix, but event timestamp is March 29
        var timestamp = new DateTimeOffset(2026, 3, 29, 14, 0, 0, TimeSpan.Zero); // Sunday March 29 UTC
        var title = StreamTitle.Format("Sunday, March 22, 2026 - Old Title", timestamp);
        title.Value.Should().Be("Sunday, March 29, 2026 - Old Title");
    }
}
