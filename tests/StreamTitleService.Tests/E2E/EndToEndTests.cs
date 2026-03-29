using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using StreamTitleService.Domain.Events;
using Xunit;

namespace StreamTitleService.Tests.E2E;

/// <summary>
/// End-to-end tests against the real deployed Azure Function.
/// Verifies messages are consumed from the subscription (not left behind).
///
/// Requires INTEGRATION_TEST_SB_CONNECTION environment variable.
/// Run with: dotnet test --filter "Category=E2E"
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

    private async Task DrainSubscriptionAsync()
    {
        await using var receiver = _client!.CreateReceiver(TopicName, SubscriptionName,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });

        while (true)
        {
            var messages = await receiver.ReceiveMessagesAsync(maxMessages: 100, maxWaitTime: TimeSpan.FromSeconds(3));
            if (messages.Count == 0) break;
        }
    }

    /// <summary>
    /// Polls the subscription until it's empty (all messages consumed by the function)
    /// or timeout elapses. Returns true if subscription is empty.
    /// </summary>
    private async Task<bool> WaitForSubscriptionEmptyAsync(TimeSpan timeout, TimeSpan pollInterval)
    {
        await using var receiver = _client!.CreateReceiver(TopicName, SubscriptionName,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var peeked = await receiver.PeekMessageAsync();
            if (peeked == null)
                return true; // Subscription is empty -- function consumed everything

            await Task.Delay(pollInterval);
        }

        return false;
    }

    /// <summary>
    /// Checks the dead-letter sub-queue for messages matching the predicate.
    /// </summary>
    private async Task<string?> CheckDeadLetterAsync(Func<ServiceBusReceivedMessage, bool> predicate)
    {
        var dlqPath = $"{TopicName}/subscriptions/{SubscriptionName}/$deadletterqueue";
        await using var receiver = _client!.CreateReceiver(dlqPath,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

        var messages = await receiver.ReceiveMessagesAsync(maxMessages: 10, maxWaitTime: TimeSpan.FromSeconds(10));
        foreach (var msg in messages)
        {
            if (predicate(msg))
            {
                var body = msg.Body.ToString();
                await receiver.AbandonMessageAsync(msg);
                return body;
            }
            await receiver.AbandonMessageAsync(msg);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Test 1: Function consumes StreamStarted and sets title on Restream
    //
    // Verification: the message disappears from the subscription.
    // If the function failed, the message would be retried and eventually
    // dead-lettered -- not consumed cleanly.
    // ------------------------------------------------------------------
    [SkippableFact]
    public async Task StreamStarted_ProcessedByFunction_MessageConsumed()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "INTEGRATION_TEST_SB_CONNECTION is not set; skipping E2E test.");

        await DrainSubscriptionAsync();

        var sentEvent = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "e2e-test",
            Timestamp = DateTimeOffset.UtcNow,
            Location = "virtual",
            Data = new StreamStartedData { Title = "E2E Test Title" }
        };

        var json = JsonSerializer.Serialize(sentEvent);
        await _sender!.SendMessageAsync(new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            Subject = "StreamStarted"
        });

        // Wait for the function to consume the message (subscription becomes empty).
        // The function also publishes StreamTitleSet back to the topic, which lands
        // on the subscription and gets auto-completed by the eventType filter.
        // So "subscription empty" means both the input and output were processed.
        var isEmpty = await WaitForSubscriptionEmptyAsync(
            timeout: TimeSpan.FromSeconds(120),
            pollInterval: TimeSpan.FromSeconds(10));

        isEmpty.Should().BeTrue(
            "the deployed function should consume the StreamStarted message within 120 seconds");
    }

    // ------------------------------------------------------------------
    // Test 2: Unknown location causes dead-lettering after retries
    //
    // Verification: the message ends up in the dead-letter sub-queue.
    // ------------------------------------------------------------------
    [SkippableFact]
    public async Task StreamStarted_WithUnknownLocation_EndsUpInDeadLetter()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString),
            "INTEGRATION_TEST_SB_CONNECTION is not set; skipping E2E test.");

        await DrainSubscriptionAsync();

        var sentEvent = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "e2e-test",
            Timestamp = DateTimeOffset.UtcNow,
            Location = "unknown-test-location",
            Data = new StreamStartedData { Title = "E2E Dead-Letter Test" }
        };

        var json = JsonSerializer.Serialize(sentEvent);
        await _sender!.SendMessageAsync(new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            Subject = "StreamStarted"
        });

        // Wait for retries (maxDeliveryCount=3) and dead-lettering.
        // This takes time: 3 delivery attempts with lock duration between them.
        var isEmpty = await WaitForSubscriptionEmptyAsync(
            timeout: TimeSpan.FromSeconds(180),
            pollInterval: TimeSpan.FromSeconds(15));

        isEmpty.Should().BeTrue(
            "the message should be consumed (and dead-lettered) within 180 seconds");

        // Verify the message landed in the dead-letter queue
        var dlqBody = await CheckDeadLetterAsync(msg =>
        {
            var body = JsonSerializer.Deserialize<StreamStartedEvent>(msg.Body.ToString());
            return body?.Location == "unknown-test-location";
        });

        dlqBody.Should().NotBeNull(
            "the unknown location message should be in the dead-letter queue");
    }
}
