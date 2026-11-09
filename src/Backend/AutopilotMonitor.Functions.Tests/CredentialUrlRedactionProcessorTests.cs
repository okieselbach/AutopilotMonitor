using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Functions.Telemetry;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Credential URLs never reach a dependency row (K20, D-424): inside a request of a marked client the row
/// keeps scheme + host and the method, whatever host the URL names; outside it nothing changes; the mark
/// does not leak into the caller's flow; the real collector pipeline carries the mark to the processor;
/// and both credential clients (webhooks, push) carry the mark.
/// </summary>
public class CredentialUrlRedactionProcessorTests
{
    private sealed class CapturingProcessor : ITelemetryProcessor
    {
        public List<ITelemetry> Received { get; } = new();
        public void Process(ITelemetry item) => Received.Add(item);
    }

    private sealed class CapturingChannel : ITelemetryChannel
    {
        private readonly List<ITelemetry> _items = new();
        public IReadOnlyList<ITelemetry> Items { get { lock (_items) return _items.ToList(); } }
        public bool? DeveloperMode { get; set; }
        public string? EndpointAddress { get; set; }
        public void Send(ITelemetry item) { lock (_items) _items.Add(item); }
        public void Flush() { }
        public void Dispose() { }
    }

    /// <summary>Stands in for the collector: tracks a dependency row from inside the request's flow.</summary>
    private sealed class TrackingInnerHandler(ITelemetryProcessor processor, DependencyTelemetry row) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            processor.Process(row);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private static DependencyTelemetry Row(string url, string method = "POST")
        => new() { Type = "Http", Data = url, Name = $"{method} {new Uri(url).AbsolutePath}", Target = new Uri(url).Authority };

    [Theory]
    [InlineData("https://web.push.apple.com/QF3abc/device-token-123", "https://web.push.apple.com")]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc:APA91", "https://fcm.googleapis.com")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAA", "https://updates.push.services.mozilla.com")]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", "https://wns2-db5p.notify.windows.com")]
    [InlineData("https://discord.com/api/webhooks/123456/discord-token", "https://discord.com")]
    [InlineData("https://hooks.slack.com/services/T000/B000/slack-secret", "https://hooks.slack.com")]
    [InlineData("https://default00000000.00.environment.api.powerplatform.com:443/powerautomate/automations/direct/workflows/abc/triggers/manual/paths/invoke?api-version=1&sp=%2Ftriggers&sv=1.0&sig=signature", "https://default00000000.00.environment.api.powerplatform.com")]
    [InlineData("https://tickets.example.test/hooks/intake?apikey=generic-secret", "https://tickets.example.test")]
    public void Redact_keeps_scheme_host_and_method_only(string url, string expected)
    {
        var dependency = Row(url);

        CredentialUrlRedactionProcessor.Redact(dependency);

        Assert.Equal(expected, dependency.Data);
        Assert.Equal("POST " + expected, dependency.Name);
    }

    [Fact]
    public void Redact_of_an_unparseable_url_keeps_only_the_target_host()
    {
        var dependency = new DependencyTelemetry { Data = "not a url /api/webhooks/1/token", Name = "POST /api/webhooks/1/token", Target = "discord.com" };

        CredentialUrlRedactionProcessor.Redact(dependency);

        Assert.Equal("discord.com", dependency.Data);
        Assert.Equal("POST discord.com", dependency.Name);
    }

    [Fact]
    public void Rows_outside_a_marked_request_are_untouched()
    {
        var next = new CapturingProcessor();
        var processor = new CredentialUrlRedactionProcessor(next);
        var dependency = Row("https://graph.microsoft.com/v1.0/devices?$filter=x", "GET");

        processor.Process(dependency);

        Assert.Same(dependency, Assert.Single(next.Received));
        Assert.Equal("https://graph.microsoft.com/v1.0/devices?$filter=x", dependency.Data);
        Assert.Equal("GET /v1.0/devices", dependency.Name);
    }

    [Fact]
    public async Task The_mark_covers_the_request_and_ends_with_it()
    {
        var next = new CapturingProcessor();
        var processor = new CredentialUrlRedactionProcessor(next);
        var inside = Row("https://discord.com/api/webhooks/123456/discord-token");
        using var client = new HttpClient(new CredentialUrlRedactionProcessor.MarkingHandler
        {
            InnerHandler = new TrackingInnerHandler(processor, inside),
        });

        await client.PostAsync("https://discord.com/api/webhooks/123456/discord-token", new StringContent("{}"));
        var after = Row("https://graph.microsoft.com/v1.0/devices", "GET");
        processor.Process(after);

        Assert.Equal("https://discord.com", inside.Data);
        Assert.Equal("https://graph.microsoft.com/v1.0/devices", after.Data);
    }

    [Fact]
    public async Task The_real_collector_pipeline_carries_the_mark_to_the_processor()
    {
        var channel = new CapturingChannel();
        using var configuration = new TelemetryConfiguration
        {
            TelemetryChannel = channel,
            ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000",
        };
        configuration.TelemetryProcessorChainBuilder.Use(next => new CredentialUrlRedactionProcessor(next)).Build();
        using var collector = new DependencyTrackingTelemetryModule();
        collector.Initialize(configuration);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = AnswerNoContentAsync(listener, connections: 2);
            var secretPath = $"http://127.0.0.1:{port}/api/webhooks/123456/discord-token";

            using (var marked = new HttpClient(new CredentialUrlRedactionProcessor.MarkingHandler { InnerHandler = new SocketsHttpHandler() }))
                (await marked.PostAsync(secretPath, new StringContent("{}"))).Dispose();
            using (var plain = new HttpClient(new SocketsHttpHandler()))
                (await plain.PostAsync(secretPath + "?unmarked=1", new StringContent("{}"))).Dispose();
            await server;

            var rows = channel.Items.OfType<DependencyTelemetry>().Where(d => d.Target?.EndsWith(":" + port) == true).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, d => d.Data == "http://127.0.0.1" && d.Name == "POST http://127.0.0.1");
            Assert.Contains(rows, d => d.Data == secretPath + "?unmarked=1");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(nameof(WebhookNotificationService))]
    [InlineData(PushHttpClientRegistration.ClientName)]
    public void Both_credential_clients_carry_the_mark(string clientName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ResiliencePolicies>();
        services.AddWebhookNotificationHttpClient();
        services.AddPushSender();
        using var provider = services.BuildServiceProvider();

        var handlers = new List<HttpMessageHandler>();
        for (HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
             handler != null;
             handler = (handler as DelegatingHandler)?.InnerHandler)
        {
            handlers.Add(handler);
        }

        Assert.Single(handlers.OfType<CredentialUrlRedactionProcessor.MarkingHandler>());
    }

    /// <summary>Answers each of <paramref name="connections"/> requests with 204 and closes the connection.</summary>
    private static async Task AnswerNoContentAsync(TcpListener listener, int connections)
    {
        for (var i = 0; i < connections; i++)
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var buffer = new byte[8192];
            var received = new StringBuilder();
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            // The request body ("{}") follows the headers; the 204 does not wait for it.
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        }
    }
}
