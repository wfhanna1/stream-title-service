using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using StreamTitleService.Domain.Events;
using Xunit;

namespace StreamTitleService.Tests.Integration;

/// <summary>
/// Integration tests against the real deployed Service Bus.
/// Requires the INTEGRATION_TEST_SB_CONNECTION environment variable to be set.
/// Run with: dotnet test --filter "Category=Integration"
/// Skip with: dotnet test --filter "Category!=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class ServiceBusIntegrationTests : IAsyncDisposable
{
    private const string TopicName = "stream-title";
    // Use a dedicated test subscription so integration tests don't compete
    // with the deployed function on the stream-title-service subscription.
    private const string SubscriptionName = "integration-test";

    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("INTEGRATION_TEST_SB_CONNECTION");

    private readonly ServiceBusClient? _client;
    private readonly ServiceBusSender? _sender;
    private readonly ServiceBusReceiver? _receiver;

    public ServiceBusIntegrationTests()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            return;

        _client = new ServiceBusClient(ConnectionString);
        _sender = _client.CreateSender(TopicName);
        _receiver = _client.CreateReceiver(TopicName, SubscriptionName,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync();
        if (_receiver is not null) await _receiver.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Test 1: Send a StreamTitleSetEvent message to the stream-title
    //         topic, receive it on the stream-title-service subscription,
    //         verify JSON deserializes correctly.
    // ------------------------------------------------------------------
    [SkippableFact]
    public async Task SendAndReceive_StreamTitleSetEvent_DeserializesCorrectly()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "INTEGRATION_TEST_SB_CONNECTION is not set; skipping integration test.");

        var sentEvent = new StreamTitleSetEvent
        {
            EventType = "StreamTitleSet",
            Source = "integration-test",
            Timestamp = new DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero),
            Location = "virtual",
            Data = new StreamTitleSetData
            {
                Title = "Friday, March 27, 2026 - Arabic Bible Study",
                TargetPlatform = "restream",
                ChannelsUpdated = 3,
                ChannelsFailed = 0
            }
        };

        var json = JsonSerializer.Serialize(sentEvent);
        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            Subject = sentEvent.EventType
        };

        await _sender!.SendMessageAsync(message);

        var received = await _receiver!.ReceiveMessageAsync(maxWaitTime: TimeSpan.FromSeconds(30));

        received.Should().NotBeNull("the message should be received within 30 seconds");

        var body = received!.Body.ToString();
        var deserialized = JsonSerializer.Deserialize<StreamTitleSetEvent>(body);

        deserialized.Should().NotBeNull();
        deserialized!.EventType.Should().Be("StreamTitleSet");
        deserialized.Source.Should().Be("integration-test");
        deserialized.Location.Should().Be("virtual");
        deserialized.Data.Title.Should().Be("Friday, March 27, 2026 - Arabic Bible Study");
        deserialized.Data.TargetPlatform.Should().Be("restream");
        deserialized.Data.ChannelsUpdated.Should().Be(3);
        deserialized.Data.ChannelsFailed.Should().Be(0);
        deserialized.Timestamp.Should().Be(sentEvent.Timestamp);
    }

    // ------------------------------------------------------------------
    // Test 2: Message properties (ContentType, Subject) are preserved
    //         through send/receive.
    // ------------------------------------------------------------------

    [SkippableFact]
    public async Task SendAndReceive_MessageProperties_ArePreserved()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "INTEGRATION_TEST_SB_CONNECTION is not set; skipping integration test.");

        var sentEvent = new StreamTitleSetEvent
        {
            EventType = "StreamTitleSet",
            Source = "integration-test",
            Timestamp = DateTimeOffset.UtcNow,
            Location = "st. mary and st. joseph",
            Data = new StreamTitleSetData
            {
                Title = "Thursday, March 26, 2026 - Divine Liturgy",
                TargetPlatform = "restream",
                ChannelsUpdated = 2,
                ChannelsFailed = 0
            }
        };

        var json = JsonSerializer.Serialize(sentEvent);
        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            Subject = sentEvent.EventType
        };

        await _sender!.SendMessageAsync(message);

        var received = await _receiver!.ReceiveMessageAsync(maxWaitTime: TimeSpan.FromSeconds(30));

        received.Should().NotBeNull("the message should be received within 30 seconds");
        received!.ContentType.Should().Be("application/json");
        received.Subject.Should().Be("StreamTitleSet");
    }

    // ------------------------------------------------------------------
    // Test 3: Send a StreamStartedEvent and verify it round-trips through
    //         the Service Bus topic/subscription with correct deserialization.
    // ------------------------------------------------------------------
    [SkippableFact]
    public async Task SendStreamStartedEvent_ReceiveAndDeserialize_RoundTrip()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "INTEGRATION_TEST_SB_CONNECTION is not set; skipping integration test.");

        var sentEvent = new StreamStartedEvent
        {
            SchemaVersion = "1",
            EventType = "StreamStarted",
            Source = "integration-test",
            Timestamp = new DateTimeOffset(2026, 3, 27, 18, 0, 0, TimeSpan.Zero),
            Location = "st. mary and st. joseph",
            TraceId = "trace-abc123",
            Data = new StreamStartedData
            {
                Title = "Friday, March 27, 2026 - Vespers"
            }
        };

        var json = JsonSerializer.Serialize(sentEvent);
        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            Subject = sentEvent.EventType
        };

        await _sender!.SendMessageAsync(message);

        var received = await _receiver!.ReceiveMessageAsync(maxWaitTime: TimeSpan.FromSeconds(30));

        received.Should().NotBeNull("the StreamStarted message should be received within 30 seconds");

        var body = received!.Body.ToString();
        var deserialized = JsonSerializer.Deserialize<StreamStartedEvent>(body);

        deserialized.Should().NotBeNull();
        deserialized!.EventType.Should().Be("StreamStarted");
        deserialized.Source.Should().Be("integration-test");
        deserialized.Location.Should().Be("st. mary and st. joseph");
        deserialized.SchemaVersion.Should().Be("1");
        deserialized.TraceId.Should().Be("trace-abc123");
        deserialized.Data.Title.Should().Be("Friday, March 27, 2026 - Vespers");
        deserialized.Timestamp.Should().Be(sentEvent.Timestamp);

        received.ContentType.Should().Be("application/json");
        received.Subject.Should().Be("StreamStarted");
    }
}
