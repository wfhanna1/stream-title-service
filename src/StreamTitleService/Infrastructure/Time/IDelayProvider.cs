namespace StreamTitleService.Infrastructure.Time;

public interface IDelayProvider
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
