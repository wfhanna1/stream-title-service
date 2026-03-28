using System.Text.Json.Serialization;

namespace StreamTitleService.Domain.Events;

public class StreamTitleFailedEvent
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "StreamTitleFailed";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "stream-title-service";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; set; }

    [JsonPropertyName("data")]
    public StreamTitleFailedData Data { get; set; } = new();
}

public class StreamTitleFailedData
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("targetPlatform")]
    public string TargetPlatform { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("channelsUpdated")]
    public int ChannelsUpdated { get; set; }

    [JsonPropertyName("channelsAttempted")]
    public int ChannelsAttempted { get; set; }
}
