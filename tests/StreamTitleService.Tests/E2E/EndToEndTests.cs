using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using StreamTitleService.Domain.Events;
using Xunit;

namespace StreamTitleService.Tests.E2E;

/// <summary>
/// End-to-end tests against the real deployed Azure Function at stream-title-svc-okg4gt72g4sfo.
/// Requires the INTEGRATION_TEST_SB_CONNECTION environment variable (Service Bus manage connection string).
/// Run with: dotnet test --filter "Category=E2E"
/// Skip with: dotnet test --filter "Category!=E2E"
/// </summary>
[Trait("Category", "E2E")]
public class EndToEndTests : IAsyncDisposable
{
    private const string TopicName = "stream-title";
    private const string SubscriptionName = "stream-title-service";

    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("INTEGRATION_TEST_SB_CONNECTION");

    private readonly ServiceBusClient? _client;
    private readonly ServiceBusSender? _sender;

    public EndToEndTests()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            return;

        _client = new ServiceBusClient(ConnectionString);
        _sender = _client.CreateSender(TopicName);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Drains all messages currently sitting on the subscription using
    // ReceiveAndDelete so they do not interfere with the test assertions.
    // ------------------------------------------------------------------
    private async Task DrainSubscriptionAsync(ServiceBusClient client)
    {
        await using var receiver = client.CreateReceiver(TopicName, SubscriptionName,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
            });

        while (true)
        {
            var messages = await receiver.ReceiveMessagesAsync(maxMessages: 100, maxWaitTime: TimeSpan.FromSeconds(3));
            if (messages.Count == 0)
                break;
        }
    }

    // ------------------------------------------------------------------
    // Polls the subscription (PeekAndLock, then abandons) until a message
    // matching the predicate appears, or the timeout elapses.
    // Returns the matching message body, or null on timeout.
    // ------------------------------------------------------------------
    private static async Task<string?> WaitForMessageAsync(
        ServiceBusClient client,
        Func<ServiceBusReceivedMessage, bool> predicate,
        TimeSpan timeout,
        TimeSpan pollInterval)
    {
        await using var receiver = client.CreateReceiver(TopicName, SubscriptionName,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var messages = await receiver.ReceiveMessagesAsync(maxMessages: 50, maxWaitTime: TimeSpan.FromSeconds(5));

            foreach (var msg in messages)
            {
                if (predicate(msg))
                {
                    var body = msg.Body.ToString();
                    // Abandon so other calls can also see it if needed.
                    await receiver.AbandonMessageAsync(msg);
                    return body;
                }

                // Not what we want -- put it back.
                await receiver.AbandonMessageAsync(msg);
            }

            if (DateTimeOffset.UtcNow + pollInterval < deadline)
                await Task.Delay(pollInterval);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Test 1: Function picks up StreamStarted and publishes StreamTitleSet
    // ------------------------------------------------------------------
    [SkippableFact]
    public async Task StreamStarted_ProcessedByFunction_TitleSetOnRestream()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "INTEGRATION_TEST_SB_CONNECTION is not set; skipping E2E test.");

        // 1. Drain stale messages so assertions are clean.
        await DrainSubscriptionAsync(_client!);

        // 2. Send a StreamStarted event with a known title.
        var sentEvent = new StreamStartedEvent
        {
            SchemaVersion = "1",
            EventType = "StreamStarted",
            Source = "e2e-test",
            Timestamp = DateTimeOffset.UtcNow,
            Location = "virtual",
            Data = new StreamStartedData
            {
                Title = "E2E Test Title"
            }
        };

        var json = JsonSerializer.Serialize(sentEvent);
        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            Subject = sentEvent.EventType
        };

        await _sender!.SendMessageAsync(message);

        // 3. Wait up to 120 seconds for the function to consume the StreamStarted
        //    message and publish a StreamTitleSet event back onto the topic.
        //    The function filters by eventType, so the StreamTitleSet message will
        //    land on the subscription and stay there (not be processed again).
        var titleSetBody = await WaitForMessageAsync(
            _client!,
            msg => msg.Subject == "StreamTitleSet",
            timeout: TimeSpan.FromSeconds(120),
            pollInterval: TimeSpan.FromSeconds(10));

        // 4. Assert that a StreamTitleSet event was received.
        titleSetBody.Should().NotBeNull(
            "the deployed function should have processed the StreamStarted event and published StreamTitleSet within 120 seconds");

        var titleSetEvent = JsonSerializer.Deserialize<StreamTitleSetEvent>(titleSetBody!);

        titleSetEvent.Should().NotBeNull();
        titleSetEvent!.EventType.Should().Be("StreamTitleSet");
        titleSetEvent.Data.TargetPlatform.Should().Be("restream");
        titleSetEvent.Data.Title.Should().Contain("E2E Test Title");
        titleSetEvent.Data.ChannelsUpdated.Should().BeGreaterThan(0);
    }

    // ------------------------------------------------------------------
    // Test 2: Unknown location causes the function to throw; after retries
    //         Service Bus dead-letters the message and a StreamTitleFailed
    //         event is published.
    // ------------------------------------------------------------------
    [SkippableFact]
    public async Task StreamStarted_WithUnknownLocation_DeadLettered()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "INTEGRATION_TEST_SB_CONNECTION is not set; skipping E2E test.");

        // 1. Drain stale messages so assertions are clean.
        await DrainSubscriptionAsync(_client!);

        // 2. Send a StreamStarted event with an unknown location.
        var sentEvent = new StreamStartedEvent
        {
            SchemaVersion = "1",
            EventType = "StreamStarted",
            Source = "e2e-test",
            Timestamp = DateTimeOffset.UtcNow,
            Location = "unknown-test-location",
            Data = new StreamStartedData
            {
                Title = "E2E Dead-Letter Test"
            }
        };

        var json = JsonSerializer.Serialize(sentEvent);
        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            Subject = sentEvent.EventType
        };

        await _sender!.SendMessageAsync(message);

        // 3. Wait up to 180 seconds for Service Bus to exhaust retries (3x) and
        //    dead-letter the message.  The function publishes StreamTitleFailed
        //    before or after the dead-letter; we look for that event on the
        //    subscription as proof that the failure path ran.
        var failedBody = await WaitForMessageAsync(
            _client!,
            msg => msg.Subject == "StreamTitleFailed",
            timeout: TimeSpan.FromSeconds(180),
            pollInterval: TimeSpan.FromSeconds(10));

        // 4. Assert that a StreamTitleFailed event was received.
        failedBody.Should().NotBeNull(
            "the deployed function should have published StreamTitleFailed after failing to process the unknown location within 180 seconds");

        var failedEvent = JsonSerializer.Deserialize<StreamTitleFailedEvent>(failedBody!);

        failedEvent.Should().NotBeNull();
        failedEvent!.EventType.Should().Be("StreamTitleFailed");
        failedEvent.Location.Should().Be("unknown-test-location");
        failedEvent.Data.Error.Should().NotBeNullOrEmpty();
    }
}
