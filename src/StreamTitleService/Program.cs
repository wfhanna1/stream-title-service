using Azure.Communication.Email;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using StreamTitleService.Application;
using StreamTitleService.Application.Ports.Inbound;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Adapters;
using StreamTitleService.Infrastructure.Configuration;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // The .NET isolated worker installs an ApplicationInsightsLoggerProvider rule pinned at
        // Warning, so host.json logLevel.StreamTitleService=Information has no effect for our
        // worker code. Drop that rule so worker logs reach App Insights at the host.json level.
        // https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide#application-insights
        services.Configure<LoggerFilterOptions>(options =>
        {
            var aiRule = options.Rules.FirstOrDefault(r =>
                r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (aiRule is not null)
                options.Rules.Remove(aiRule);
        });

        // Domain configuration
        services.AddSingleton<ILocationPlatformMapper, LocationPlatformMapping>();

        // HttpClient for RestreamTokenProvider (token refresh calls)
        services.AddHttpClient("TokenClient");

        // Register RestreamTokenProvider (Singleton) -- loads Key Vault secrets lazily at first resolution
        var keyVaultUri = Environment.GetEnvironmentVariable("KEY_VAULT_URI");
        services.AddSingleton<ITokenProvider>(sp =>
        {
            if (string.IsNullOrEmpty(keyVaultUri))
                throw new InvalidOperationException("KEY_VAULT_URI environment variable is not set");

            var credential = new DefaultAzureCredential();
            var kvClient = new SecretClient(new Uri(keyVaultUri), credential);

            var refreshToken = kvClient.GetSecret("restream-refresh-token").Value.Value;
            var clientId = kvClient.GetSecret("restream-client-id").Value.Value;
            var clientSecret = kvClient.GetSecret("restream-client-secret").Value.Value;

            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("TokenClient");
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<RestreamTokenProvider>>();

            // Callback to persist new refresh tokens to Key Vault (survives cold starts)
            Func<string, Task> onRefreshTokenUpdated = async newToken =>
            {
                await kvClient.SetSecretAsync("restream-refresh-token", newToken);
            };

            return new RestreamTokenProvider(httpClient, refreshToken, clientId, clientSecret, logger, onRefreshTokenUpdated);
        });

        // HttpClient for RestreamClient with Polly resilience
        services.AddHttpClient("RestreamClient", client =>
        {
            client.BaseAddress = new Uri("https://api.restream.io/v2/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddPolicyHandler(GetRetryPolicy())
        .AddPolicyHandler(GetCircuitBreakerPolicy());

        // RestreamClient (Singleton)
        services.AddSingleton<RestreamClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("RestreamClient");
            var tokenProvider = sp.GetRequiredService<ITokenProvider>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<RestreamClient>>();
            return new RestreamClient(httpClient, tokenProvider, logger);
        });

        // YouTubeClient -- uses LazyYouTubeServiceWrapper to defer blob credential loading
        // until the first YouTube API call (avoids async-over-sync deadlock at startup)
        var blobStorageConnection = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION");

        // Wait-with-retry tuning for the YouTube broadcast race (broadcast goes
        // active a few seconds after OBS reports streaming started). Operator-tunable
        // via app settings; sensible defaults preserve prior behavior of the wait code.
        var youtubeMaxWaitSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("YOUTUBE_BROADCAST_MAX_WAIT_SECONDS"), out var ytWait)
            ? ytWait
            : 30;
        var youtubePollIntervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("YOUTUBE_BROADCAST_POLL_INTERVAL_SECONDS"), out var ytPoll)
            ? ytPoll
            : 2;
        var youtubeMaxWait = TimeSpan.FromSeconds(youtubeMaxWaitSeconds);
        var youtubePollInterval = TimeSpan.FromSeconds(youtubePollIntervalSeconds);

        services.AddSingleton<YouTubeClient>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<YouTubeClient>>();
            if (!string.IsNullOrEmpty(blobStorageConnection))
            {
                var wrapper = new LazyYouTubeServiceWrapper(async () =>
                {
                    var ytBlobName = Environment.GetEnvironmentVariable("YOUTUBE_TOKEN_BLOB_NAME") ?? "st-anthony-token.json";
                    var blobClient = new BlobClient(blobStorageConnection, "youtube-tokens", ytBlobName);
                    var tokenProviderLogger = sp.GetService<Microsoft.Extensions.Logging.ILogger<BlobStorageYouTubeTokenProvider>>();
                    var youTubeTokenProvider = new BlobStorageYouTubeTokenProvider(blobClient, tokenProviderLogger);
                    var youTubeService = await youTubeTokenProvider.CreateYouTubeServiceAsync(CancellationToken.None);
                    return new GoogleYouTubeServiceWrapper(youTubeService);
                });
                return new YouTubeClient(wrapper, logger, youtubeMaxWait, youtubePollInterval);
            }

            logger?.LogWarning("BLOB_STORAGE_CONNECTION is not set. YouTube path will throw if invoked.");
            return new YouTubeClient(new NullYouTubeServiceWrapper(), logger, youtubeMaxWait, youtubePollInterval);
        });

        // Platform client dictionary routing TargetPlatform -> ITitlePlatformClient
        services.AddSingleton<IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient>>(sp =>
        {
            return new Dictionary<TargetPlatform, ITitlePlatformClient>
            {
                [TargetPlatform.Restream] = sp.GetRequiredService<RestreamClient>(),
                [TargetPlatform.YouTube] = sp.GetRequiredService<YouTubeClient>()
            };
        });

        // AcsAlertNotifier (Singleton)
        var acsConnectionString = Environment.GetEnvironmentVariable("ACS_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(acsConnectionString))
        {
            var acsSender = Environment.GetEnvironmentVariable("ACS_SENDER") ?? "";
            var acsRecipients = (Environment.GetEnvironmentVariable("ACS_RECIPIENTS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            services.AddSingleton<IAlertNotifier>(sp =>
            {
                var emailClient = new EmailClient(acsConnectionString);
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<AcsAlertNotifier>>();
                return new AcsAlertNotifier(emailClient, acsSender, acsRecipients, logger);
            });
        }
        else
        {
            services.AddSingleton<IAlertNotifier, NullAlertNotifier>();
        }

        // StreamTitleHandler (Singleton) with configurable staleness threshold
        var stalenessSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("STALENESS_THRESHOLD_SECONDS"), out var parsed)
            ? parsed
            : 90;

        services.AddSingleton<IStreamTitleHandler>(sp =>
        {
            var locationMapping = sp.GetRequiredService<ILocationPlatformMapper>();
            var clients = sp.GetRequiredService<IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient>>();
            var alertNotifier = sp.GetRequiredService<IAlertNotifier>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<StreamTitleHandler>>();
            return new StreamTitleHandler(locationMapping, clients, alertNotifier, stalenessSeconds, logger);
        });
    })
    .Build();

host.Run();

// Polly policies
static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    var jitter = new Random();
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt =>
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s
                var jitterMs = jitter.Next(0, 500);
                var total = delay + TimeSpan.FromMilliseconds(jitterMs);
                return total > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : total;
            });
}

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 3,
            durationOfBreak: TimeSpan.FromSeconds(60));
}

// Null implementations for optional/stub dependencies

internal sealed class NullAlertNotifier : IAlertNotifier
{
    public Task SendFailureAlertAsync(string title, string error, CancellationToken ct)
        => Task.CompletedTask;
}

internal sealed class NullYouTubeServiceWrapper : IYouTubeServiceWrapper
{
    public Task<string> GetMyChannelIdAsync(CancellationToken ct)
        => throw new InvalidOperationException("YouTube credentials not configured. Set up blob storage token.");

    public Task<List<LiveBroadcastInfo>> ListActiveBroadcastsAsync(CancellationToken ct)
        => throw new InvalidOperationException("YouTube credentials not configured. Set up blob storage token.");

    public Task<VideoSnippetInfo> GetVideoSnippetAsync(string videoId, CancellationToken ct)
        => throw new InvalidOperationException("YouTube credentials not configured. Set up blob storage token.");

    public Task UpdateVideoSnippetAsync(string videoId, string newTitle, string description, string channelId, List<string> tags, CancellationToken ct)
        => throw new InvalidOperationException("YouTube credentials not configured. Set up blob storage token.");
}
