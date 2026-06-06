namespace StreamTitleService.Infrastructure.Adapters;

public sealed record RestreamRetryPolicy(
    int MaxAttempts,
    TimeSpan InitialVerifyWait,
    IReadOnlyList<TimeSpan> BackoffSchedule)
{
    public static RestreamRetryPolicy Defaults { get; } = new(
        MaxAttempts: 3,
        InitialVerifyWait: TimeSpan.FromSeconds(5),
        BackoffSchedule: new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20)
        });
}
