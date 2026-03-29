namespace StreamTitleService.Infrastructure.Adapters;

/// <summary>
/// Lazy-initializing decorator for IYouTubeServiceWrapper.
/// Defers blob storage credential loading until the first API call,
/// avoiding async-over-sync deadlocks at DI startup time.
/// </summary>
public class LazyYouTubeServiceWrapper : IYouTubeServiceWrapper
{
    private readonly Func<Task<IYouTubeServiceWrapper>> _factory;
    private IYouTubeServiceWrapper? _inner;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LazyYouTubeServiceWrapper(Func<Task<IYouTubeServiceWrapper>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    private async Task<IYouTubeServiceWrapper> GetInnerAsync()
    {
        if (_inner is not null) return _inner;

        await _lock.WaitAsync();
        try
        {
            _inner ??= await _factory();
            return _inner;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetMyChannelIdAsync(CancellationToken ct)
        => await (await GetInnerAsync()).GetMyChannelIdAsync(ct);

    public async Task<List<LiveBroadcastInfo>> ListActiveBroadcastsAsync(CancellationToken ct)
        => await (await GetInnerAsync()).ListActiveBroadcastsAsync(ct);

    public async Task<VideoSnippetInfo> GetVideoSnippetAsync(string videoId, CancellationToken ct)
        => await (await GetInnerAsync()).GetVideoSnippetAsync(videoId, ct);

    public async Task UpdateVideoSnippetAsync(string videoId, string newTitle, string description, string channelId, List<string> tags, CancellationToken ct)
        => await (await GetInnerAsync()).UpdateVideoSnippetAsync(videoId, newTitle, description, channelId, tags, ct);
}
