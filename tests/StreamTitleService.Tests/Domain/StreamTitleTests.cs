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
}
