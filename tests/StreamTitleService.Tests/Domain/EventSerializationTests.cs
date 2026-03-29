using System.Text.Json;
using FluentAssertions;
using StreamTitleService.Domain.Events;
using Xunit;

namespace StreamTitleService.Tests.Domain;

public class EventSerializationTests
{
    private static readonly JsonSerializerOptions DefaultOptions = new();

    [Fact]
    public void StreamStartedEvent_Serializes_WithCorrectFieldNames()
    {
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "stream-title-service",
            Timestamp = new DateTimeOffset(2026, 3, 27, 10, 0, 0, TimeSpan.Zero),
            Location = "us-east-1",
            TraceId = "trace-abc",
            SpanId = "span-xyz",
            ParentSpanId = "parent-123",
            Data = new StreamStartedData { Title = "Sunday Liturgy" }
        };

        var json = JsonSerializer.Serialize(evt, DefaultOptions);
        var doc = JsonDocument.Parse(json).RootElement;

        doc.TryGetProperty("eventType", out _).Should().BeTrue("eventType field must be present");
        doc.TryGetProperty("source", out _).Should().BeTrue("source field must be present");
        doc.TryGetProperty("timestamp", out _).Should().BeTrue("timestamp field must be present");
        doc.TryGetProperty("location", out _).Should().BeTrue("location field must be present");
        doc.TryGetProperty("traceId", out _).Should().BeTrue("traceId field must be present");
        doc.TryGetProperty("spanId", out _).Should().BeTrue("spanId field must be present");
        doc.TryGetProperty("parentSpanId", out _).Should().BeTrue("parentSpanId field must be present");
        doc.TryGetProperty("data", out var data).Should().BeTrue("data field must be present");
        data.TryGetProperty("title", out _).Should().BeTrue("data.title field must be present");

        doc.GetProperty("eventType").GetString().Should().Be("StreamStarted");
        doc.GetProperty("source").GetString().Should().Be("stream-title-service");
        doc.GetProperty("location").GetString().Should().Be("us-east-1");
        doc.GetProperty("traceId").GetString().Should().Be("trace-abc");
        doc.GetProperty("spanId").GetString().Should().Be("span-xyz");
        doc.GetProperty("parentSpanId").GetString().Should().Be("parent-123");
        data.GetProperty("title").GetString().Should().Be("Sunday Liturgy");
    }

    [Fact]
    public void StreamStartedEvent_WithNullOptionalFields_SerializesCorrectly()
    {
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "stream-title-service",
            Timestamp = DateTimeOffset.UtcNow,
            Location = "us-east-1",
            TraceId = null,
            SpanId = null,
            ParentSpanId = null,
            Data = new StreamStartedData { Title = null }
        };

        var json = JsonSerializer.Serialize(evt, DefaultOptions);
        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow("null optional fields must not break serialization");

        var doc = JsonDocument.Parse(json).RootElement;

        doc.TryGetProperty("traceId", out var traceId).Should().BeTrue();
        traceId.ValueKind.Should().Be(JsonValueKind.Null);

        doc.TryGetProperty("spanId", out var spanId).Should().BeTrue();
        spanId.ValueKind.Should().Be(JsonValueKind.Null);

        doc.TryGetProperty("parentSpanId", out var parentSpanId).Should().BeTrue();
        parentSpanId.ValueKind.Should().Be(JsonValueKind.Null);

        doc.GetProperty("data").TryGetProperty("title", out var title).Should().BeTrue();
        title.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void StreamStartedEvent_RoundTrip_PreservesAllFields()
    {
        var original = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "stream-title-service",
            Timestamp = new DateTimeOffset(2026, 3, 27, 10, 0, 0, TimeSpan.Zero),
            Location = "us-east-1",
            TraceId = "trace-abc",
            SpanId = "span-xyz",
            ParentSpanId = "parent-123",
            Data = new StreamStartedData { Title = "Sunday Liturgy" }
        };

        var json = JsonSerializer.Serialize(original, DefaultOptions);
        var restored = JsonSerializer.Deserialize<StreamStartedEvent>(json, DefaultOptions);

        restored.Should().NotBeNull();
        restored!.EventType.Should().Be(original.EventType);
        restored.Source.Should().Be(original.Source);
        restored.Timestamp.Should().Be(original.Timestamp);
        restored.Location.Should().Be(original.Location);
        restored.TraceId.Should().Be(original.TraceId);
        restored.SpanId.Should().Be(original.SpanId);
        restored.ParentSpanId.Should().Be(original.ParentSpanId);
        restored.Data.Should().NotBeNull();
        restored.Data.Title.Should().Be(original.Data.Title);
    }

    [Fact]
    public void StreamStartedEvent_Deserialize_WithExtraFields_IgnoresThem()
    {
        var json = """
            {
                "eventType": "StreamStarted",
                "source": "stream-title-service",
                "timestamp": "2026-03-27T10:00:00+00:00",
                "location": "us-east-1",
                "traceId": null,
                "spanId": null,
                "parentSpanId": null,
                "unknownTopLevelField": "should be ignored",
                "data": {
                    "title": "Sunday Liturgy",
                    "extraDataField": 42
                }
            }
            """;

        var act = () => JsonSerializer.Deserialize<StreamStartedEvent>(json, DefaultOptions);
        act.Should().NotThrow("unknown fields in JSON must be ignored during deserialization");

        var result = JsonSerializer.Deserialize<StreamStartedEvent>(json, DefaultOptions);
        result.Should().NotBeNull();
        result!.EventType.Should().Be("StreamStarted");
        result.Data.Title.Should().Be("Sunday Liturgy");
    }
}
