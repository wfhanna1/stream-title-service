using System.Diagnostics;
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
    private readonly IEventPublisher _eventPublisher;
    private readonly IAlertNotifier _alertNotifier;
    private readonly int _stalenessThresholdSeconds;
    private readonly TitleResolver _titleResolver = new();
    private readonly ILogger<StreamTitleHandler>? _logger;

    public StreamTitleHandler(
        ILocationPlatformMapper locationMapping,
        IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient> clients,
        IEventPublisher eventPublisher,
        IAlertNotifier alertNotifier,
        int stalenessThresholdSeconds = 90,
        ILogger<StreamTitleHandler>? logger = null)
    {
        _locationMapping = locationMapping;
        _clients = clients;
        _eventPublisher = eventPublisher;
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
            await PublishFailedAsync(evt, evt.Data.Title ?? "(default)", "unknown", ex.Message, 0, 0, ct);
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
            await PublishFailedAsync(evt, title.Value, platform.Value, error, 0, 0, ct);
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
            await PublishFailedAsync(evt, title.Value, platform.Value, ex.Message, 0, 0, ct);
            await _alertNotifier.SendFailureAlertAsync(title.Value, ex.Message, ct);
            throw;
        }

        try
        {
            await _eventPublisher.PublishTitleSetAsync(new StreamTitleSetEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Location = location.Value,
                TraceId = Activity.Current?.TraceId.ToString(),
                SpanId = Activity.Current?.SpanId.ToString(),
                ParentSpanId = evt.SpanId,
                Data = new StreamTitleSetData
                {
                    Title = title.Value,
                    TargetPlatform = platform.Value,
                    ChannelsUpdated = result.ChannelsUpdated,
                    ChannelsFailed = result.ChannelsFailed
                }
            }, ct);
        }
        catch (Exception pubEx)
        {
            _logger?.LogError(pubEx, "Failed to publish StreamTitleSet event");
        }

        _logger?.LogInformation("Title set: '{Title}' on {Platform} ({Updated} channels)",
            title.Value, platform.Value, result.ChannelsUpdated);
    }

    private async Task PublishFailedAsync(
        StreamStartedEvent evt, string title, string platform,
        string error, int updated, int attempted, CancellationToken ct)
    {
        try
        {
            await _eventPublisher.PublishTitleFailedAsync(new StreamTitleFailedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Location = evt.Location,
                TraceId = Activity.Current?.TraceId.ToString(),
                SpanId = Activity.Current?.SpanId.ToString(),
                ParentSpanId = evt.SpanId,
                Data = new StreamTitleFailedData
                {
                    Title = title,
                    TargetPlatform = platform,
                    Error = error,
                    ChannelsUpdated = updated,
                    ChannelsAttempted = attempted
                }
            }, ct);
        }
        catch (Exception pubEx)
        {
            _logger?.LogError(pubEx, "Failed to publish StreamTitleFailed event");
        }
    }
}
