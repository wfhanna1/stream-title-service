namespace StreamTitleService.Domain.ValueObjects;

public sealed record TargetPlatform
{
    public static readonly TargetPlatform Restream = new("restream");
    public static readonly TargetPlatform YouTube = new("youtube");

    public string Value { get; }

    private TargetPlatform(string value) => Value = value;

    public override string ToString() => Value;
}
