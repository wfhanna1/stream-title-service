using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.ValueObjects;
using TimeZoneConverter;

namespace StreamTitleService.Domain.Services;

public class TitleResolver
{
    private static readonly TimeZoneInfo Eastern =
        TZConvert.GetTimeZoneInfo("America/New_York");

    public StreamTitle Resolve(StreamStartedEvent evt)
    {
        var suffix = !string.IsNullOrWhiteSpace(evt.Data.Title)
            ? evt.Data.Title
            : GenerateDefaultSuffix(evt.Timestamp);

        return StreamTitle.Format(suffix, evt.Timestamp);
    }

    private static string GenerateDefaultSuffix(DateTimeOffset timestamp)
    {
        var eastern = TimeZoneInfo.ConvertTime(timestamp, Eastern);

        var isSaturdayEvening =
            eastern.DayOfWeek == DayOfWeek.Saturday &&
            eastern.Hour >= 17;

        return isSaturdayEvening
            ? "Vespers and Midnight Praises"
            : "Divine Liturgy";
    }
}
