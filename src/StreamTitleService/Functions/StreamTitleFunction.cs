using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Inbound;
using StreamTitleService.Domain.Events;

namespace StreamTitleService.Functions;

public class StreamTitleFunction
{
    private readonly IStreamTitleHandler _handler;
    private readonly ILogger<StreamTitleFunction>? _logger;

    public StreamTitleFunction(
        IStreamTitleHandler handler,
        ILogger<StreamTitleFunction>? logger = null)
    {
        _handler = handler;
        _logger = logger;
    }

    [Function(nameof(StreamTitleFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger(
            "%SERVICE_BUS_TOPIC%",
            "%SERVICE_BUS_SUBSCRIPTION%",
            Connection = "SERVICE_BUS_CONNECTION")]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "Received Service Bus message {MessageId} on stream-title topic",
            message.MessageId);

        var body = message.Body.ToString();
        var evt = JsonSerializer.Deserialize<StreamStartedEvent>(body)
            ?? throw new InvalidOperationException("Failed to deserialize StreamStartedEvent: null result");

        if (evt.SchemaVersion != null && evt.SchemaVersion != "1")
        {
            _logger?.LogError("Unrecognized schema version: {Version}. Dead-lettering.", evt.SchemaVersion);
            throw new InvalidOperationException($"Unrecognized schema version: {evt.SchemaVersion}");
        }

        if (evt.EventType != "StreamStarted")
        {
            _logger?.LogWarning("Ignoring event with type '{EventType}', expected 'StreamStarted'", evt.EventType);
            return;
        }

        _logger?.LogInformation(
            "Processing StreamStartedEvent for location {Location} from {Source}",
            evt.Location, evt.Source);

        await _handler.HandleAsync(evt, cancellationToken);
    }
}
