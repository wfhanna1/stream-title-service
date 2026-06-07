using System.Diagnostics;
using FluentAssertions;
using StreamTitleService.Infrastructure.Time;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure.Time;

public class SystemDelayProviderTests
{
    [Fact]
    public async Task DelayAsync_WithPositiveTimeSpan_ShouldDelayAtLeastThatMuch()
    {
        var provider = new SystemDelayProvider();
        var sw = Stopwatch.StartNew();

        await provider.DelayAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        sw.Stop();
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
    }
}
