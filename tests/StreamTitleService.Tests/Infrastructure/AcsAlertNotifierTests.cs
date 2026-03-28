using Azure;
using Azure.Communication.Email;
using FluentAssertions;
using Moq;
using StreamTitleService.Infrastructure.Adapters;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class AcsAlertNotifierTests
{
    private readonly Mock<EmailClient> _emailClient = new();
    private const string SenderAddress = "noreply@example.com";
    private static readonly string[] Recipients = ["alerts@example.com"];

    [Fact]
    public async Task SendFailureAlert_WhenEmailClientThrows_ShouldNotPropagate()
    {
        _emailClient
            .Setup(c => c.SendAsync(
                It.IsAny<WaitUntil>(),
                It.IsAny<EmailMessage>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ACS connection failed"));

        var notifier = new AcsAlertNotifier(_emailClient.Object, SenderAddress, Recipients);

        // Best-effort: should complete without throwing even when EmailClient throws
        var act = () => notifier.SendFailureAlertAsync("Arabic Bible Study", "Network error", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendFailureAlert_ShouldConstructCorrectSubjectAndBody()
    {
        EmailMessage? capturedMessage = null;
        _emailClient
            .Setup(c => c.SendAsync(
                It.IsAny<WaitUntil>(),
                It.IsAny<EmailMessage>(),
                It.IsAny<CancellationToken>()))
            .Callback<WaitUntil, EmailMessage, CancellationToken>((_, msg, _) => capturedMessage = msg)
            .ReturnsAsync((EmailSendOperation)null!);

        var notifier = new AcsAlertNotifier(_emailClient.Object, SenderAddress, Recipients);
        await notifier.SendFailureAlertAsync("Arabic Bible Study", "YouTube quota exceeded", CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.Content.Subject.Should().Contain("Arabic Bible Study");
        capturedMessage.Content.PlainText.Should().Contain("Arabic Bible Study");
        capturedMessage.Content.PlainText.Should().Contain("YouTube quota exceeded");
    }
}
