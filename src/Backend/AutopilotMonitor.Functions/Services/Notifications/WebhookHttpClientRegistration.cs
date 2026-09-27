using System;
using System.Net.Http;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    public static class WebhookHttpClientRegistration
    {
        /// <summary>
        /// Registers the typed HttpClient of <see cref="WebhookNotificationService"/>. Its primary
        /// handler connects only through <see cref="SsrfGuard.ConnectToPublicAddress"/>: the webhook
        /// SSRF gate lives on the connection, so it covers every request this client sends.
        /// </summary>
        public static IHttpClientBuilder AddWebhookNotificationHttpClient(this IServiceCollection services) =>
            services.AddHttpClient<WebhookNotificationService>()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                    ConnectCallback = SsrfGuard.ConnectToPublicAddress,
                })
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(15))
                .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().Notification);
    }
}
