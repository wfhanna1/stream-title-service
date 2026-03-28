namespace StreamTitleService.Infrastructure.Adapters;

/// <summary>
/// Data transfer records for YouTube API interactions.
/// These decouple our code from the Google SDK types for testability.
/// </summary>
public record LiveBroadcastInfo(string VideoId, string ChannelId, string Title);
public record VideoSnippetInfo(
    string VideoId,
    string Title,
    string Description,
    string ChannelId,
    List<string> Tags);

/// <summary>
/// Abstraction over the Google YouTube Data API v3 for testability.
/// The real implementation wraps YouTubeService from Google.Apis.YouTube.v3.
/// </summary>
public interface IYouTubeServiceWrapper
{
    Task<string> GetMyChannelIdAsync(CancellationToken ct);
    Task<List<LiveBroadcastInfo>> ListActiveBroadcastsAsync(CancellationToken ct);
    Task<VideoSnippetInfo> GetVideoSnippetAsync(string videoId, CancellationToken ct);
    Task UpdateVideoSnippetAsync(
        string videoId, string newTitle, string description,
        string channelId, List<string> tags, CancellationToken ct);
}
