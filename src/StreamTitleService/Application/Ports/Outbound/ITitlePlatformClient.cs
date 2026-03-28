namespace StreamTitleService.Application.Ports.Outbound;
public record TitleUpdateResult(int ChannelsUpdated, int ChannelsFailed);
public interface ITitlePlatformClient
{
    Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct);
}
