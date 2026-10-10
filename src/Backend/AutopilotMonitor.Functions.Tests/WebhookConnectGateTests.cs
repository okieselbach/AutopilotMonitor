using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The webhook SSRF gate on real loopback sockets. DNS is injected: <c>rebind.example.test</c> does
/// not exist, so a socket reaching a listener proves it used the gate's answer rather than a
/// second resolution.
/// </summary>
public class WebhookConnectGateTests
{
    private const string Host = "rebind.example.test";

    private sealed class CountingResolver
    {
        private readonly IPAddress[] _answer;
        public List<string> Calls { get; } = new();
        public CountingResolver(params string[] answer) => _answer = answer.Select(IPAddress.Parse).ToArray();

        public Task<IPAddress[]> Resolve(string host, CancellationToken _)
        {
            Calls.Add(host);
            return Task.FromResult(_answer);
        }
    }

    private static HttpClient ClientThrough(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connect,
        X509Certificate2? trustedServerCertificate = null)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = connect };
        if (trustedServerCertificate != null)
        {
            // Pinned to the test's own self-signed certificate — validation stays on, only the trust anchor differs.
            var pinned = trustedServerCertificate.GetCertHashString();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == pinned;
        }
        // Short timeout: with a broken gate the TLS handshake waits on a listener that never accepts.
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int PortOf(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    [Fact]
    public async Task PrivateAnswer_FailsClosed_WithoutOpeningASocket()
    {
        var listener = StartListener();
        try
        {
            var resolver = new CountingResolver("127.0.0.1");
            using var client = ClientThrough(SsrfGuard.CreateConnectCallback(resolver.Resolve));

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://{Host}:{PortOf(listener)}/hook"));

            var refused = Assert.IsType<SsrfException>(ex.InnerException);
            Assert.Contains("private or reserved", refused.Message);
            Assert.Equal(new[] { Host }, resolver.Calls);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task MixedAnswer_WithOnePrivateAddress_FailsClosed()
    {
        var resolver = new CountingResolver("1.1.1.1", "169.254.169.254");
        using var client = ClientThrough(SsrfGuard.CreateConnectCallback(resolver.Resolve));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://{Host}/hook"));

        Assert.IsType<SsrfException>(ex.InnerException);
    }

    [Fact]
    public async Task PrivateIpLiteral_FailsClosed_WithoutResolving()
    {
        var listener = StartListener();
        try
        {
            var resolver = new CountingResolver("1.1.1.1");
            using var client = ClientThrough(SsrfGuard.CreateConnectCallback(resolver.Resolve));

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://127.0.0.1:{PortOf(listener)}/hook"));

            Assert.IsType<SsrfException>(ex.InnerException);
            Assert.Empty(resolver.Calls);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task PlainHttp_IsRefused_BeforeResolving()
    {
        var resolver = new CountingResolver("1.1.1.1");
        using var client = ClientThrough(SsrfGuard.CreateConnectCallback(resolver.Resolve));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://{Host}/hook"));

        Assert.Contains("HTTPS", Assert.IsType<SsrfException>(ex.InnerException).Message);
        Assert.Empty(resolver.Calls);
    }

    [Fact]
    public async Task UnresolvableOrEmptyAnswer_FailsClosed()
    {
        Task<IPAddress[]> NotFound(string _, CancellationToken __) => throw new SocketException((int)SocketError.HostNotFound);
        using (var client = ClientThrough(SsrfGuard.CreateConnectCallback(NotFound)))
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://{Host}/hook"));
            Assert.Contains("resolve", Assert.IsType<SsrfException>(ex.InnerException).Message);
        }

        using (var client = ClientThrough(SsrfGuard.CreateConnectCallback(new CountingResolver().Resolve)))
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://{Host}/hook"));
            Assert.Contains("no addresses", Assert.IsType<SsrfException>(ex.InnerException).Message);
        }
    }

    [Fact]
    public async Task AllowedAnswer_ConnectsToExactlyThatAddress_AndTlsStillTargetsTheRequestHost()
    {
        using var certificate = CreateServerCertificate(Host);
        var listener = StartListener();
        try
        {
            string? serverName = null;
            var server = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                using var tls = new SslStream(tcp.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
                serverName = tls.TargetHostName;
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    var read = await tls.ReadAsync(buffer);
                    if (read == 0) break;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
            });

            // The classifier is widened to reach the loopback listener; the socket path is the production one.
            var resolver = new CountingResolver("127.0.0.1");
            using var client = ClientThrough(SsrfGuard.CreateConnectCallback(resolver.Resolve, _ => false), certificate);

            var response = await client.GetAsync($"https://{Host}:{PortOf(listener)}/hook");
            await server;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
            Assert.Equal(Host, serverName);
            Assert.Equal(new[] { Host }, resolver.Calls);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void WebhookClient_PrimaryHandler_ConnectsOnlyThroughTheGate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ResiliencePolicies>();
        services.AddWebhookNotificationHttpClient();
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(WebhookNotificationService));
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Same(SsrfGuard.ConnectToPublicAddress, primary.ConnectCallback);
        Assert.False(primary.AllowAutoRedirect);
    }

    [Fact]
    public async Task NotificationPolicy_DoesNotRetryARefusedDestination()
    {
        var policies = new ResiliencePolicies(NullLoggerFactory.Instance);
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => policies.Notification.ExecuteAsync(() =>
        {
            attempts++;
            throw new HttpRequestException("refused", new SsrfException("Webhook URL targets a private or reserved network."));
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task TestNotification_ReportsTheRefusal()
    {
        var resolver = new CountingResolver("10.0.0.8");
        var webhook = new WebhookNotificationService(ClientThrough(SsrfGuard.CreateConnectCallback(resolver.Resolve)),
            NullLogger<WebhookNotificationService>.Instance);

        var result = await webhook.SendAsync($"https://{Host}/hook", WebhookProviderType.GenericJson,
            new NotificationAlert { Title = "T", Summary = "S" });

        Assert.False(result.Success);
        Assert.Equal("Webhook URL targets a private or reserved network.", result.Message);
    }

    private static X509Certificate2 CreateServerCertificate(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Round-trip through PKCS#12: SChannel cannot serve a certificate whose key is ephemeral.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
