using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace DLSSGManager;

/// <summary>
/// The HTTP clients the downloader uses, from one <see cref="IHttpClientFactory"/>.
///
/// <list type="bullet">
/// <item><see cref="Payload"/>: downloads and version probes. Transient failures (connection resets,
/// 5xx, 408, 429) are retried with jittered backoff by the resilience pipeline; GitHub resets a
/// noticeable share of connections on some networks. A dropped stream after the headers arrived is
/// resumed by <see cref="HttpTransfer"/> instead, which knows how many bytes it already has.</item>
/// <item><see cref="Probe"/>: speed tests. No retries — a probe is a single timed sample.</item>
/// </list>
///
/// Both share the same primary handler: redirects off (each hop is re-checked by
/// <see cref="HttpTransfer.GetCheckedAsync"/>) and connections opened by
/// <see cref="DownloadPolicy.ConnectAsync"/>.
/// </summary>
public static class PayloadHttp
{
    private const string PayloadClient = "payload";
    private const string ProbeClient = "probe";

    private static readonly Lazy<IHttpClientFactory> Factory = new(Build);

    /// <summary>Overrides the handler for tests; null restores the real one.</summary>
    internal static Func<HttpMessageHandler>? TestHandler { get; set; }

    public static HttpClient Payload() => Create(PayloadClient);

    public static HttpClient Probe() => Create(ProbeClient);

    private static HttpClient Create(string name)
    {
        if (TestHandler is { } test) return new HttpClient(test(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return Factory.Value.CreateClient(name);
    }

    private static IHttpClientFactory Build()
    {
        var services = new ServiceCollection();

        services.AddHttpClient(PayloadClient, Configure)
            .ConfigurePrimaryHttpMessageHandler(PrimaryHandler)
            .AddResilienceHandler("payload", pipeline =>
            {
                // Outer bound on one request including its retries; bodies are streamed afterwards
                // and guarded by HttpTransfer's stall timeout, not by this.
                pipeline.AddTimeout(TimeSpan.FromSeconds(90));
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    UseJitter = true,
                });
                pipeline.AddTimeout(TimeSpan.FromSeconds(30));
            });

        services.AddHttpClient(ProbeClient, Configure)
            .ConfigurePrimaryHttpMessageHandler(PrimaryHandler);

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static void Configure(HttpClient client)
    {
        // Header waits are bounded by the pipeline; body reads by the stall timeout.
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSSGManager/" + AppVersion.Label.TrimStart('v'));
    }

    private static HttpMessageHandler PrimaryHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectCallback = DownloadPolicy.ConnectAsync,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
