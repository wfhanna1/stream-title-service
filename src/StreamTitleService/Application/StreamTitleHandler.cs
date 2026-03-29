using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Inbound;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.Exceptions;
using StreamTitleService.Domain.Services;
using StreamTitleService.Domain.ValueObjects;

namespace StreamTitleService.Application;

public class StreamTitleHandler : IStreamTitleHandler
{
    private readonly ILocationPlatformMapper _locationMapping;
    private readonly IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient> _clients;
    private readonly IAlertNotifier _alertNotifier;
    private readonly int _stalenessThresholdSeconds;
    private readonly TitleResolver _titleResolver = new();
    private readonly ILogger<StreamTitleHandler>? _logger;

    public StreamTitleHandler(
        ILocationPlatformMapper locationMapping,
        IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient> clients,
        IAlertNotifier alertNotifier,
        int stalenessThresholdSeconds = 90,
        ILogger<StreamTitleHandler>? logger = null)
    {
        _locationMapping = locationMapping;
        _clients = clients;
        _alertNotifier = alertNotifier;
        _stalenessThresholdSeconds = stalenessThresholdSeconds;
        _logger = logger;
    }

    public async Task HandleAsync(StreamStartedEvent evt, CancellationToken ct)
    {
        // Check staleness
        var age = DateTimeOffset.UtcNow - evt.Timestamp;
        if (age.TotalSeconds > _stalenessThresholdSeconds)
        {
            _logger?.LogWarning("Skipping stale event from {Source}, age {AgeSec}s exceeds threshold {Threshold}s",
                evt.Source, (int)age.TotalSeconds, _stalenessThresholdSeconds);
            return;
        }

        // Resolve location (throws UnknownLocationException for unknown)
        Location location;
        try
        {
            location = new Location(evt.Location);
        }
        catch (UnknownLocationException ex)
        {
            _logger?.LogError("StreamTitleFailed: Location={Location} is unknown. Event will be dead-lettered.", evt.Location);
            try
            {
                await _alertNotifier.SendFailureAlertAsync(
                    evt.Data.Title ?? "(default)", ex.Message, ct);
            }
            catch (Exception alertEx)
            {
                _logger?.LogError(alertEx, "Failed to send alert for unknown location");
            }
            throw;
        }

        // Resolve title
        var title = _titleResolver.Resolve(evt);

        // Route to platform
        var platform = _locationMapping.GetPlatform(location);

        if (!_clients.TryGetValue(platform, out var client))
        {
            var error = $"No client registered for platform: {platform.Value}";
            _logger?.LogError("StreamTitleFailed: Title={Title}, Platform={Platform}, Error={Error}",
                title.Value, platform.Value, error);
            await _alertNotifier.SendFailureAlertAsync(title.Value, error, ct);
            throw new InvalidOperationException(error);
        }

        // Set title on platform
        TitleUpdateResult result;
        try
        {
            result = await client.SetTitleAsync(title.Value, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "StreamTitleFailed: Title={Title}, Platform={Platform}, Error={Error}",
                title.Value, platform.Value, ex.Message);
            await _alertNotifier.SendFailureAlertAsync(title.Value, ex.Message, ct);
            throw;
        }

        _logger?.LogInformation("StreamTitleSet: Title={Title}, Platform={Platform}, ChannelsUpdated={Updated}, ChannelsFailed={Failed}",
            title.Value, platform.Value, result.ChannelsUpdated, result.ChannelsFailed);
    }
}
