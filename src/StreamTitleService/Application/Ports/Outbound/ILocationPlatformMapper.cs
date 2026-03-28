using StreamTitleService.Domain.ValueObjects;

namespace StreamTitleService.Application.Ports.Outbound;

public interface ILocationPlatformMapper
{
    TargetPlatform GetPlatform(Location location);
}
