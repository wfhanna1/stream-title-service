using Azure.Communication.Email;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
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

        // Domain configuration
        services.AddSingleton<ILocationPlatformMapper, LocationPlatformMapping>();

        // Restream credentials from Key Vault
        var keyVaultUri = Environment.GetEnvironmentVariable("KEY_VAULT_URI")
            ?? throw new InvalidOperationException("KEY_VAULT_URI environment variable is not set");

        var credential = new DefaultAzureCredential();
        var kvClient = new SecretClient(new Uri(keyVaultUri), credential);

        var refreshToken = kvClient.GetSecret("restream-refresh-token").Value.Value;
        var clientId = kvClient.GetSecret("restream-client-id").Value.Value;
        var clientSecret = kvClient.GetSecret("restream-client-secret").Value.Value;

        // HttpClient for RestreamTokenProvider (token refresh calls)
        services.AddHttpClient("TokenClient");

        // Register RestreamTokenProvider (Singleton) with Key Vault credentials
        services.AddSingleton<ITokenProvider>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("TokenClient");
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<RestreamTokenProvider>>();
            return new RestreamTokenProvider(httpClient, refreshToken, clientId, clientSecret, logger);
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

        // YouTubeClient -- wire real implementation when BLOB_STORAGE_CONNECTION is set
        var blobStorageConnection = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION");
        services.AddSingleton<YouTubeClient>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<YouTubeClient>>();
            if (!string.IsNullOrEmpty(blobStorageConnection))
            {
                var blobClient = new BlobClient(blobStorageConnection, "youtube-tokens", "token.json");
                var tokenProviderLogger = sp.GetService<Microsoft.Extensions.Logging.ILogger<BlobStorageYouTubeTokenProvider>>();
                var youTubeTokenProvider = new BlobStorageYouTubeTokenProvider(blobClient, tokenProviderLogger);
                var youTubeService = youTubeTokenProvider.CreateYouTubeServiceAsync(CancellationToken.None).GetAwaiter().GetResult();
                return new YouTubeClient(new GoogleYouTubeServiceWrapper(youTubeService), logger);
            }

            logger?.LogWarning("BLOB_STORAGE_CONNECTION is not set. YouTube path will throw if invoked.");
            return new YouTubeClient(new NullYouTubeServiceWrapper(), logger);
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

        // ServiceBus sender for stream-title topic
        var serviceBusConnection = Environment.GetEnvironmentVariable("SERVICE_BUS_CONNECTION")
            ?? throw new InvalidOperationException("SERVICE_BUS_CONNECTION environment variable is not set");

        services.AddSingleton(new ServiceBusClient(serviceBusConnection));
        var sbTopic = Environment.GetEnvironmentVariable("SERVICE_BUS_TOPIC") ?? "stream-title";
        services.AddSingleton(sp =>
            sp.GetRequiredService<ServiceBusClient>().CreateSender(sbTopic));

        // ServiceBusEventPublisher (Singleton)
        services.AddSingleton<IEventPublisher>(sp =>
        {
            var sender = sp.GetRequiredService<ServiceBusSender>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ServiceBusEventPublisher>>();
            return new ServiceBusEventPublisher(sender, logger);
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
            var eventPublisher = sp.GetRequiredService<IEventPublisher>();
            var alertNotifier = sp.GetRequiredService<IAlertNotifier>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<StreamTitleHandler>>();
            return new StreamTitleHandler(locationMapping, clients, eventPublisher, alertNotifier, stalenessSeconds, logger);
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
