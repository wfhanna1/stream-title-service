using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;

namespace StreamTitleService.Infrastructure.Adapters;

/// <summary>
/// YouTube Data API v3 client for setting livestream titles.
///
/// Ported from YT-Title-Updater's YouTubeClient (Python). The exact API sequence:
/// 1. channels.list(part=id, mine=true) -> get authenticated user's channel ID
/// 2. liveBroadcasts.list(part=snippet,status, broadcastStatus=active) -> find active broadcast for our channel
/// 3. videos.list(part=snippet, id=videoId) -> get FULL snippet (must preserve all fields)
/// 4. videos.update(part=snippet, body={id, snippet with new title}) -> update title only
///
/// Credentials: Google OAuth2 UserCredential stored as JSON in Azure Blob Storage.
/// </summary>
public class YouTubeClient : ITitlePlatformClient
{
    private readonly IYouTubeServiceWrapper _youtube;
    private readonly ILogger<YouTubeClient>? _logger;

    public YouTubeClient(
        IYouTubeServiceWrapper youtube,
        ILogger<YouTubeClient>? logger = null)
    {
        _youtube = youtube;
        _logger = logger;
    }

    public async Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct)
    {
        // Step 1: Get authenticated user's channel ID
        var myChannelId = await _youtube.GetMyChannelIdAsync(ct);
        _logger?.LogInformation("Authenticated as channel {ChannelId}", myChannelId);

        // Step 2: Find active broadcast matching our channel
        var broadcasts = await _youtube.ListActiveBroadcastsAsync(ct);
        var myBroadcast = broadcasts.FirstOrDefault(b => b.ChannelId == myChannelId);

        if (myBroadcast == null)
        {
            _logger?.LogWarning("No active broadcast found for channel {ChannelId}", myChannelId);
            return new TitleUpdateResult(0, 0);
        }

        _logger?.LogInformation("Found active broadcast {VideoId}: '{Title}'",
            myBroadcast.VideoId, myBroadcast.Title);

        // Step 3: Get current video snippet (must preserve all fields)
        var snippet = await _youtube.GetVideoSnippetAsync(myBroadcast.VideoId, ct);

        // Step 4: Update only the title, preserving description, tags, channelId, etc.
        await _youtube.UpdateVideoSnippetAsync(
            myBroadcast.VideoId,
            title,
            snippet.Description,
            snippet.ChannelId,
            snippet.Tags,
            ct);

        _logger?.LogInformation("Updated YouTube video {VideoId} title to '{Title}'",
            myBroadcast.VideoId, title);

        return new TitleUpdateResult(1, 0);
    }
}
