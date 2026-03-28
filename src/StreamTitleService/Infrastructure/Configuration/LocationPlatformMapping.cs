using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.ValueObjects;

namespace StreamTitleService.Infrastructure.Configuration;

public class LocationPlatformMapping : ILocationPlatformMapper
{
    private static readonly Dictionary<string, TargetPlatform> Mapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["virtual"] = TargetPlatform.Restream,
        ["st. mary and st. joseph"] = TargetPlatform.Restream,
        ["st. anthony chapel"] = TargetPlatform.YouTube
    };

    public TargetPlatform GetPlatform(Location location)
    {
        return Mapping[location.Value];
    }
}
