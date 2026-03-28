namespace StreamTitleService.Application.Ports.Outbound;
public interface ITokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}
