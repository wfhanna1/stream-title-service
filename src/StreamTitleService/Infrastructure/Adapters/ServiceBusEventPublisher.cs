using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.Events;

namespace StreamTitleService.Infrastructure.Adapters;

public class ServiceBusEventPublisher : IEventPublisher
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusEventPublisher>? _logger;

    public ServiceBusEventPublisher(
        ServiceBusSender sender,
        ILogger<ServiceBusEventPublisher>? logger = null)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task PublishTitleSetAsync(StreamTitleSetEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt);
        var message = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = evt.EventType
        };

        SetDiagnosticId(message);

        _logger?.LogInformation("Publishing {EventType} for title {Title}",
            evt.EventType, evt.Data.Title);

        await _sender.SendMessageAsync(message, ct);
    }

    public async Task PublishTitleFailedAsync(StreamTitleFailedEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt);
        var message = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = evt.EventType
        };

        SetDiagnosticId(message);

        _logger?.LogInformation("Publishing {EventType} for title {Title}",
            evt.EventType, evt.Data.Title);

        await _sender.SendMessageAsync(message, ct);
    }

    private static void SetDiagnosticId(ServiceBusMessage message)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            message.ApplicationProperties["Diagnostic-Id"] = activity.Id;
        }
    }
}
