using Azure.Communication.Email;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;

namespace StreamTitleService.Infrastructure.Adapters;

public class AcsAlertNotifier : IAlertNotifier
{
    private readonly EmailClient _emailClient;
    private readonly string _senderAddress;
    private readonly string[] _recipients;
    private readonly ILogger<AcsAlertNotifier>? _logger;

    public AcsAlertNotifier(
        EmailClient emailClient,
        string senderAddress,
        string[] recipients,
        ILogger<AcsAlertNotifier>? logger = null)
    {
        _emailClient = emailClient;
        _senderAddress = senderAddress;
        _recipients = recipients;
        _logger = logger;
    }

    public async Task SendFailureAlertAsync(string title, string error, CancellationToken ct)
    {
        try
        {
            var subject = $"[Stream Title Service] Title update failed: {title}";
            var body = $"Title update failed for: {title}\n\nError: {error}";

            var emailRecipients = new EmailRecipients(
                _recipients.Select(r => new EmailAddress(r)).ToList());

            var content = new EmailContent(subject)
            {
                PlainText = body
            };

            var message = new EmailMessage(_senderAddress, emailRecipients, content);

            _logger?.LogInformation("Sending failure alert for title {Title}", title);

            await _emailClient.SendAsync(Azure.WaitUntil.Started, message, ct);

            _logger?.LogInformation("Failure alert sent for title {Title}", title);
        }
        catch (Exception ex)
        {
            // Best-effort: alerting should never break processing
            _logger?.LogError(ex, "Failed to send failure alert for title {Title}", title);
        }
    }
}
