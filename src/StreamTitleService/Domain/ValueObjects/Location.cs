using StreamTitleService.Domain.Exceptions;

namespace StreamTitleService.Domain.ValueObjects;

public sealed record Location
{
    private static readonly HashSet<string> KnownLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "virtual",
        "st. mary and st. joseph",
        "st. anthony chapel"
    };

    public string Value { get; }

    public Location(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.ToLowerInvariant();

        if (!KnownLocations.Contains(normalized))
            throw new UnknownLocationException(normalized);

        Value = normalized;
    }

    public override string ToString() => Value;
}
