using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

namespace StreamTitleService.Infrastructure.Adapters;

/// <summary>
/// Real implementation of IYouTubeServiceWrapper using Google.Apis.YouTube.v3.
/// </summary>
public class GoogleYouTubeServiceWrapper : IYouTubeServiceWrapper
{
    /// <summary>
    /// YouTube category ID for "People and Blogs". Required when updating video snippets.
    /// </summary>
    private const string PeopleAndBlogsCategoryId = "22";

    private readonly YouTubeService _service;

    public GoogleYouTubeServiceWrapper(YouTubeService service)
    {
        _service = service;
    }

    public async Task<string> GetMyChannelIdAsync(CancellationToken ct)
    {
        var request = _service.Channels.List("id");
        request.Mine = true;
        var response = await request.ExecuteAsync(ct);

        if (response.Items == null || response.Items.Count == 0)
            throw new InvalidOperationException("Could not retrieve authenticated channel ID");

        return response.Items[0].Id;
    }

    public async Task<List<LiveBroadcastInfo>> ListActiveBroadcastsAsync(CancellationToken ct)
    {
        var request = _service.LiveBroadcasts.List("snippet,status");
        request.BroadcastStatus = LiveBroadcastsResource.ListRequest.BroadcastStatusEnum.Active;
        var response = await request.ExecuteAsync(ct);

        return (response.Items ?? new List<LiveBroadcast>())
            .Select(b => new LiveBroadcastInfo(
                b.Id,
                b.Snippet.ChannelId,
                b.Snippet.Title))
            .ToList();
    }

    public async Task<VideoSnippetInfo> GetVideoSnippetAsync(string videoId, CancellationToken ct)
    {
        var request = _service.Videos.List("snippet");
        request.Id = videoId;
        var response = await request.ExecuteAsync(ct);

        if (response.Items == null || response.Items.Count == 0)
            throw new InvalidOperationException($"Could not retrieve video details for {videoId}");

        var snippet = response.Items[0].Snippet;
        return new VideoSnippetInfo(
            videoId,
            snippet.Title,
            snippet.Description ?? "",
            snippet.ChannelId,
            snippet.Tags?.ToList() ?? new List<string>());
    }

    public async Task UpdateVideoSnippetAsync(
        string videoId, string newTitle, string description,
        string channelId, List<string> tags, CancellationToken ct)
    {
        var video = new Video
        {
            Id = videoId,
            Snippet = new VideoSnippet
            {
                Title = newTitle,
                Description = description,
                ChannelId = channelId,
                Tags = tags,
                CategoryId = PeopleAndBlogsCategoryId
            }
        };

        var request = _service.Videos.Update(video, "snippet");
        await request.ExecuteAsync(ct);
    }
}
