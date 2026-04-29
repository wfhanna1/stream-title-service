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
/// Wait-with-retry on step 2: YouTube does not register a broadcast as
/// active until its RTMP ingest connects, which can lag the OBS WebSocket
/// "streaming started" ack by 5-30s. Without the retry the function
/// silently no-ops on early invocations and the visible title never updates.
///
/// Credentials: Google OAuth2 UserCredential stored as JSON in Azure Blob Storage.
/// </summary>
public class YouTubeClient : ITitlePlatformClient
{
    private readonly IYouTubeServiceWrapper _youtube;
    private readonly ILogger<YouTubeClient>? _logger;
    private readonly TimeSpan _maxBroadcastWait;
    private readonly TimeSpan _broadcastPollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public YouTubeClient(
        IYouTubeServiceWrapper youtube,
        ILogger<YouTubeClient>? logger = null,
        TimeSpan? maxBroadcastWait = null,
        TimeSpan? broadcastPollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _youtube = youtube;
        _logger = logger;
        _maxBroadcastWait = maxBroadcastWait ?? TimeSpan.FromSeconds(30);
        _broadcastPollInterval = broadcastPollInterval ?? TimeSpan.FromSeconds(2);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct)
    {
        // Step 1: Get authenticated user's channel ID
        var myChannelId = await _youtube.GetMyChannelIdAsync(ct);
        _logger?.LogInformation("Authenticated as channel {ChannelId}", myChannelId);

        // Step 2: Wait for an active broadcast on our channel.
        // YouTube's broadcastStatus=active flips a few seconds after RTMP ingest connects;
        // poll until we find one or the budget runs out.
        var myBroadcast = await WaitForActiveBroadcastAsync(myChannelId, ct);

        if (myBroadcast == null)
        {
            _logger?.LogWarning(
                "No active broadcast found for channel {ChannelId} after waiting {WaitSec}s",
                myChannelId, (int)_maxBroadcastWait.TotalSeconds);
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

    private async Task<LiveBroadcastInfo?> WaitForActiveBroadcastAsync(
        string myChannelId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _maxBroadcastWait;
        int attempt = 0;
        while (true)
        {
            attempt++;
            var broadcasts = await _youtube.ListActiveBroadcastsAsync(ct);
            var match = broadcasts.FirstOrDefault(b => b.ChannelId == myChannelId);
            if (match != null)
            {
                if (attempt > 1)
                {
                    _logger?.LogInformation(
                        "Active broadcast appeared on attempt {Attempt} for channel {ChannelId}",
                        attempt, myChannelId);
                }
                return match;
            }

            if (DateTimeOffset.UtcNow >= deadline)
                return null;

            _logger?.LogDebug(
                "No active broadcast yet for channel {ChannelId} (attempt {Attempt}); sleeping {IntervalMs}ms",
                myChannelId, attempt, (int)_broadcastPollInterval.TotalMilliseconds);
            await _delayAsync(_broadcastPollInterval, ct);
        }
    }
}
