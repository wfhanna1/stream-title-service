using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Moq;
using StreamTitleService.Domain.Events;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class ServiceBusEventPublisherTests
{
    private readonly Mock<ServiceBusSender> _sender = new();

    [Fact]
    public async Task PublishTitleSet_ShouldSerializeCorrectJson()
    {
        ServiceBusMessage? capturedMessage = null;
        _sender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        var evt = new StreamTitleSetEvent
        {
            Location = "virtual",
            Timestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
            Data = new StreamTitleSetData
            {
                Title = "Arabic Bible Study",
                TargetPlatform = "YouTube",
                ChannelsUpdated = 1,
                ChannelsFailed = 0
            }
        };

        var publisher = new ServiceBusEventPublisher(_sender.Object);
        await publisher.PublishTitleSetAsync(evt, CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        var json = capturedMessage!.Body.ToString();
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("eventType").GetString().Should().Be("StreamTitleSet");
        doc.RootElement.GetProperty("data").GetProperty("title").GetString().Should().Be("Arabic Bible Study");
        doc.RootElement.GetProperty("data").GetProperty("targetPlatform").GetString().Should().Be("YouTube");
        doc.RootElement.GetProperty("data").GetProperty("channelsUpdated").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task PublishTitleSet_ShouldSetContentTypeAndSubject()
    {
        ServiceBusMessage? capturedMessage = null;
        _sender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        var evt = new StreamTitleSetEvent
        {
            Data = new StreamTitleSetData { Title = "Test Title" }
        };

        var publisher = new ServiceBusEventPublisher(_sender.Object);
        await publisher.PublishTitleSetAsync(evt, CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.ContentType.Should().Be("application/json");
        capturedMessage.Subject.Should().Be("StreamTitleSet");
    }

    [Fact]
    public async Task PublishTitleFailed_ShouldSetSubjectToStreamTitleFailed()
    {
        ServiceBusMessage? capturedMessage = null;
        _sender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        var evt = new StreamTitleFailedEvent
        {
            Data = new StreamTitleFailedData
            {
                Title = "Test Title",
                Error = "Something went wrong"
            }
        };

        var publisher = new ServiceBusEventPublisher(_sender.Object);
        await publisher.PublishTitleFailedAsync(evt, CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.Subject.Should().Be("StreamTitleFailed");
        capturedMessage.ContentType.Should().Be("application/json");
    }
}
