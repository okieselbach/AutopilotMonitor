using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Push;
using Microsoft.Extensions.DependencyInjection;

namespace AutopilotMonitor.Functions.Services.Push;

public static class PushHttpClientRegistration
{
    public const string ClientName = "push";

    /// <summary>
    /// The push sender's HttpClient: same connect-time SSRF gate as the webhook client (D-286 —
    /// the anonymous pair route stores a caller-chosen URL, so the gate must sit on the
    /// connection), no redirects, short timeouts, and deliberately WITHOUT the Notification
    /// retry policy — push services send Retry-After and throttle senders that re-POST in 2 s;
    /// the library classifies once and retries 5xx exactly once (K15). The sender is a singleton
    /// (it caches VAPID tokens per push-service origin), so the pooled connection lifetime keeps
    /// DNS changes from going unnoticed.
    /// </summary>
    public static IServiceCollection AddPushSender(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectCallback = SsrfGuard.ConnectToPublicAddress,
            })
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(10));

        services.AddSingleton(sp => new WebPushSender(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName)));
        return services;
    }
}
