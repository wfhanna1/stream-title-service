namespace StreamTitleService.Application.Ports.Outbound;
public interface IAlertNotifier
{
    Task SendFailureAlertAsync(string title, string error, CancellationToken ct);
}
