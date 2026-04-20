using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using StreamTitleService.Application.Ports.Inbound;
using StreamTitleService.Domain.Events;
using StreamTitleService.Functions;
using Xunit;

namespace StreamTitleService.Tests.Functions;

public class StreamTitleFunctionTests
{
    private readonly Mock<IStreamTitleHandler> _handler = new();
    private readonly Mock<ILogger<StreamTitleFunction>> _logger = new();

    private static ServiceBusReceivedMessage CreateMessage(string body)
    {
        // ServiceBusReceivedMessage cannot be directly instantiated; use the factory method
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(Encoding.UTF8.GetBytes(body)),
            contentType: "application/json");
    }

    [Fact]
    public async Task Run_ValidJsonMessage_DeserializesAndCallsHandler()
    {
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "test",
            Timestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
            Location = "virtual",
            Data = new StreamStartedData { Title = "Arabic Bible Study" }
        };
        var json = JsonSerializer.Serialize(evt);
        var message = CreateMessage(json);

        var function = new StreamTitleFunction(_handler.Object);
        await function.RunAsync(message, CancellationToken.None);

        _handler.Verify(h => h.HandleAsync(
            It.Is<StreamStartedEvent>(e =>
                e.EventType == "StreamStarted" &&
                e.Location == "virtual" &&
                e.Data.Title == "Arabic Bible Study"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_InvalidJson_ThrowsException()
    {
        var message = CreateMessage("not valid json {{{");

        var function = new StreamTitleFunction(_handler.Object);
        var act = () => function.RunAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        _handler.Verify(h => h.HandleAsync(
            It.IsAny<StreamStartedEvent>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_EmptyBody_ShouldThrow()
    {
        var message = CreateMessage("");

        var function = new StreamTitleFunction(_handler.Object);
        var act = () => function.RunAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        _handler.Verify(h => h.HandleAsync(
            It.IsAny<StreamStartedEvent>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_MissingRequiredFields_ShouldThrowDueToValidation()
    {
        // JSON with only eventType; timestamp will default to DateTimeOffset.MinValue, which fails validation
        var json = """{"eventType":"StreamStarted"}""";
        var message = CreateMessage(json);

        var function = new StreamTitleFunction(_handler.Object);
        var act = () => function.RunAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _handler.Verify(h => h.HandleAsync(
            It.IsAny<StreamStartedEvent>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_ExtraUnknownFields_ShouldIgnoreAndProcess()
    {
        // JSON contains unknown properties that don't exist on StreamStartedEvent
        var json = """{"eventType":"StreamStarted","source":"test","timestamp":"2026-03-27T12:00:00Z","location":"virtual","unknownField":"ignored","anotherField":42,"data":{"title":"My Title"}}""";
        var message = CreateMessage(json);

        _handler
            .Setup(h => h.HandleAsync(It.IsAny<StreamStartedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var function = new StreamTitleFunction(_handler.Object);
        await function.RunAsync(message, CancellationToken.None);

        _handler.Verify(h => h.HandleAsync(
            It.Is<StreamStartedEvent>(e =>
                e.EventType == "StreamStarted" &&
                e.Source == "test" &&
                e.Data.Title == "My Title"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WrongEventType_ShouldSkipProcessing()
    {
        var json = """{"eventType":"StreamTitleSet","source":"stream-title-service","data":{"title":"Some Title"}}""";
        var message = CreateMessage(json);

        var function = new StreamTitleFunction(_handler.Object);
        await function.RunAsync(message, CancellationToken.None);

        _handler.Verify(h => h.HandleAsync(
            It.IsAny<StreamStartedEvent>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_UnrecognizedSchemaVersion_ShouldThrow()
    {
        var json = """{"schemaVersion":"2","eventType":"StreamStarted","source":"test","data":{"title":"Some Title"}}""";
        var message = CreateMessage(json);

        var function = new StreamTitleFunction(_handler.Object);
        var act = () => function.RunAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unrecognized schema version*");

        _handler.Verify(h => h.HandleAsync(
            It.IsAny<StreamStartedEvent>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // TDD Cycle 1: Timestamp validation
    [Fact]
    public async Task Run_DefaultTimestamp_ShouldThrow()
    {
        // timestamp defaults to DateTimeOffset.MinValue if missing from JSON
        var json = """{"eventType":"StreamStarted","source":"test","location":"virtual","data":{}}""";
        var function = new StreamTitleFunction(_handler.Object, _logger.Object);
        var act = () => function.Run(json);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Timestamp*");
    }

    // TDD Cycle 2: Title max length validation
    [Fact]
    public async Task Run_TitleExceedsMaxLength_ShouldThrow()
    {
        var longTitle = new string('a', 201);
        var json = $$$"""{"eventType":"StreamStarted","source":"test","timestamp":"2026-03-27T12:00:00Z","location":"virtual","data":{"title":"{{{longTitle}}}"}}""";
        var function = new StreamTitleFunction(_handler.Object, _logger.Object);
        var act = () => function.Run(json);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*exceeds*");
    }

    // TDD Cycle 3: Location validation
    [Fact]
    public async Task Run_EmptyLocation_ShouldThrow()
    {
        var json = """{"eventType":"StreamStarted","source":"test","timestamp":"2026-03-27T12:00:00Z","location":"","data":{}}""";
        var function = new StreamTitleFunction(_handler.Object, _logger.Object);
        var act = () => function.Run(json);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Location*");
    }

    // Fix #8: schema version validation -- explicit v1 and null (implicit v1) should both process
    [Fact]
    public async Task Run_SchemaVersion1_ShouldProcess()
    {
        var json = """{"eventType":"StreamStarted","source":"test","timestamp":"2026-03-27T12:00:00Z","location":"virtual","schemaVersion":"1","data":{"title":"Test"}}""";
        _handler.Setup(h => h.HandleAsync(It.IsAny<StreamStartedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var function = new StreamTitleFunction(_handler.Object, _logger.Object);
        await function.Run(json);
        _handler.Verify(h => h.HandleAsync(It.IsAny<StreamStartedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ShouldLogRawMessageBody()
    {
        var json = """{"eventType":"StreamStarted","source":"test","timestamp":"2026-03-27T12:00:00Z","location":"virtual","data":{"title":"Test Title"}}""";
        _handler.Setup(h => h.HandleAsync(It.IsAny<StreamStartedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var function = new StreamTitleFunction(_handler.Object, _logger.Object);
        await function.Run(json);

        _logger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Test Title")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }

    [Fact]
    public async Task Run_NullSchemaVersion_ShouldProcess()
    {
        // v1 is implicit (no field present)
        var json = """{"eventType":"StreamStarted","source":"test","timestamp":"2026-03-27T12:00:00Z","location":"virtual","data":{"title":"Test"}}""";
        _handler.Setup(h => h.HandleAsync(It.IsAny<StreamStartedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var function = new StreamTitleFunction(_handler.Object, _logger.Object);
        await function.Run(json);
        _handler.Verify(h => h.HandleAsync(It.IsAny<StreamStartedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
