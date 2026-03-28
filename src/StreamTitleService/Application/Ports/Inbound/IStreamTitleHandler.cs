using StreamTitleService.Domain.Events;
namespace StreamTitleService.Application.Ports.Inbound;
public interface IStreamTitleHandler
{
    Task HandleAsync(StreamStartedEvent evt, CancellationToken ct);
}
