using System.Text.RegularExpressions;
using StreamTitleService.Domain.Constants;

namespace StreamTitleService.Domain.ValueObjects;

public sealed record StreamTitle
{
    // Matches: "Monday, March 29, 2026 - " (day of week, comma, full date, dash)
    private static readonly Regex DatePrefixPattern = new(
        @"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\s+\w+\s+\d{1,2},\s+\d{4}\s+-\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Value { get; }

    private StreamTitle(string value) => Value = value;

    public static StreamTitle Format(string suffix, DateTimeOffset eventTimestamp)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            throw new ArgumentException("Title suffix cannot be empty.", nameof(suffix));

        // Strip existing date prefix if present (prevents doubling)
        var cleanSuffix = DatePrefixPattern.Replace(suffix, "");

        var easternTime = TimeZoneInfo.ConvertTime(eventTimestamp, TimeZones.Eastern);
        var datePrefix = easternTime.ToString("dddd, MMMM dd, yyyy");

        return new StreamTitle($"{datePrefix} - {cleanSuffix}");
    }

    public override string ToString() => Value;
}
