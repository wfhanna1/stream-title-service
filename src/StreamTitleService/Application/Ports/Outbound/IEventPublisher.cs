using StreamTitleService.Domain.Events;
namespace StreamTitleService.Application.Ports.Outbound;
public interface IEventPublisher
{
    Task PublishTitleSetAsync(StreamTitleSetEvent evt, CancellationToken ct);
    Task PublishTitleFailedAsync(StreamTitleFailedEvent evt, CancellationToken ct);
}
