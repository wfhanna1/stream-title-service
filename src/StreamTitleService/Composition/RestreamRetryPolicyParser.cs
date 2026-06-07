using StreamTitleService.Infrastructure.Adapters;

namespace StreamTitleService.Composition;

public static class RestreamRetryPolicyParser
{
    private const string MaxAttemptsKey = "RESTREAM_VERIFY_MAX_ATTEMPTS";
    private const string InitialWaitKey = "RESTREAM_VERIFY_INITIAL_WAIT_SECONDS";
    private const string BackoffKey = "RESTREAM_VERIFY_BACKOFF_SECONDS";

    public static RestreamRetryPolicy FromEnvironment(IDictionary<string, string?> env)
    {
        var defaults = RestreamRetryPolicy.Defaults;

        var maxAttempts = TryGetInt(env, MaxAttemptsKey, defaults.MaxAttempts);
        var initialWaitSeconds = TryGetInt(env, InitialWaitKey, (int)defaults.InitialVerifyWait.TotalSeconds);
        var backoff = TryGetCsvSeconds(env, BackoffKey, defaults.BackoffSchedule);

        return new RestreamRetryPolicy(
            MaxAttempts: maxAttempts,
            InitialVerifyWait: TimeSpan.FromSeconds(initialWaitSeconds),
            BackoffSchedule: backoff);
    }

    private static int TryGetInt(IDictionary<string, string?> env, string key, int fallback)
        => env.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : fallback;

    private static IReadOnlyList<TimeSpan> TryGetCsvSeconds(
        IDictionary<string, string?> env, string key, IReadOnlyList<TimeSpan> fallback)
    {
        if (!env.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<TimeSpan>(parts.Length);
        foreach (var p in parts)
        {
            if (int.TryParse(p, out var s)) result.Add(TimeSpan.FromSeconds(s));
            else return fallback;
        }
        return result;
    }
}
