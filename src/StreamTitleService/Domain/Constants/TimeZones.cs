using TimeZoneConverter;

namespace StreamTitleService.Domain.Constants;

public static class TimeZones
{
    public static readonly TimeZoneInfo Eastern = TZConvert.GetTimeZoneInfo("America/New_York");
}
