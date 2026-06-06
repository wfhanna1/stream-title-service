using StreamTitleService.Infrastructure.Time;

namespace StreamTitleService.Tests.TestDoubles;

public sealed class RecordingDelayProvider : IDelayProvider
{
    private readonly List<TimeSpan> _recorded = new();

    public IReadOnlyList<TimeSpan> Recorded => _recorded;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        _recorded.Add(delay);
        return Task.CompletedTask;
    }
}
