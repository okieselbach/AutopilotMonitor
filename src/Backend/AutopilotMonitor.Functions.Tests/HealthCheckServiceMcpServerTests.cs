using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Tests.GraphResolution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Service-level tests for the MCP-server HealthCheck card. Drives
/// <see cref="HealthCheckService.CheckMcpServerAsync"/> through a stubbed HTTP handler
/// and asserts the card's status/message/details shape. Reuses the
/// <see cref="StubHttpMessageHandler"/> from the Graph-resolver suite.
/// </summary>
public class HealthCheckServiceMcpServerTests
{
    [Fact]
    public async Task Check_HealthyResponseWithVersion_ReportsHealthyAndSurfacesVersion()
    {
        var svc = BuildService(HttpStatusCode.OK, "{\"status\":\"healthy\",\"version\":\"1.4.0\"}");

        var check = await svc.CheckMcpServerAsync();

        Assert.Equal("MCP Server", check.Name);
        Assert.Equal("healthy", check.Status);
        Assert.Contains("reachable", check.Message);
        Assert.Equal("1.4.0", check.Details!["Version"]);
    }

    [Fact]
    public async Task Check_HealthyResponseWithoutVersion_ReportsHealthyWithNoDetails()
    {
        var svc = BuildService(HttpStatusCode.OK, "{\"status\":\"healthy\"}");

        var check = await svc.CheckMcpServerAsync();

        Assert.Equal("healthy", check.Status);
        Assert.Null(check.Details);
    }

    [Fact]
    public async Task Check_NonSuccessStatus_ReportsUnhealthyWithoutLeakingUrl()
    {
        var svc = BuildService(HttpStatusCode.ServiceUnavailable, "");

        var check = await svc.CheckMcpServerAsync();

        Assert.Equal("unhealthy", check.Status);
        Assert.Contains("503", check.Message);
        Assert.DoesNotContain("http", check.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check_WithEndpointUrl_SurfacesServerUrlAlongsideVersion()
    {
        var svc = BuildService(HttpStatusCode.OK, "{\"status\":\"healthy\",\"version\":\"1.4.0\"}");

        var check = await svc.CheckMcpServerAsync(includeEndpointUrl: true);

        // The Version detail must merge into — not replace — the Server URL detail.
        Assert.Equal("https://mcp.example.test", check.Details!["Server URL"]);
        Assert.Equal("1.4.0", check.Details["Version"]);
    }

    [Fact]
    public async Task Check_WithEndpointUrl_SurfacesServerUrlEvenWhenUnhealthy()
    {
        var svc = BuildService(HttpStatusCode.ServiceUnavailable, "");

        var check = await svc.CheckMcpServerAsync(includeEndpointUrl: true);

        Assert.Equal("unhealthy", check.Status);
        Assert.Equal("https://mcp.example.test", check.Details!["Server URL"]);
    }

    [Fact]
    public async Task Check_Timeout_ReportsWarmingSoTheCallerCanRePoll()
    {
        var svc = BuildServiceWithFactory(
            new ThrowingHttpClientFactory(new TaskCanceledException("timeout")));

        var check = await svc.CheckMcpServerAsync();

        // A scaled-to-zero container that did not answer inside the budget is "warming",
        // NOT a fault: the caller re-checks and the activation we triggered keeps running.
        Assert.Equal("warming", check.Status);
        // The default budget is pinned here because it is set nowhere else in the repo —
        // the code default IS the live production value.
        Assert.Contains("3s", check.Message);
    }

    [Fact]
    public async Task Check_Timeout_HonoursConfiguredBudget()
    {
        var svc = BuildServiceWithFactory(
            new ThrowingHttpClientFactory(new TaskCanceledException("timeout")),
            extraConfig: new Dictionary<string, string?> { ["McpServerHealthTimeoutSeconds"] = "7" });

        var check = await svc.CheckMcpServerAsync();

        Assert.Equal("warming", check.Status);
        Assert.Contains("7s", check.Message);
    }

    [Fact]
    public async Task Check_ConnectionFailure_ReportsWarningNotWarmingAndKeepsTheHostOut()
    {
        var svc = BuildServiceWithFactory(
            new ThrowingHttpClientFactory(new System.Net.Http.HttpRequestException("no such host mcp.example.test")));

        var check = await svc.CheckMcpServerAsync();

        // Reaching the network and failing to connect is a real warning — distinct from
        // the expected cold-start path, so it must not be softened to "warming".
        Assert.Equal("warning", check.Status);
        Assert.DoesNotContain("http", check.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mcp.example.test", check.Message);
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(750, false)]
    [InlineData(751, true)]
    [InlineData(2400, true)]
    public void McpReachableMessage_marksOnlyAnswersAboveTheWarmThresholdAsColdStart(long elapsedMs, bool expectColdStart)
    {
        var message = HealthCheckService.McpReachableMessage(elapsedMs);

        // Both arms keep the word "reachable" — the healthy-path assertions rely on it.
        Assert.Contains("reachable", message);
        Assert.Contains($"{elapsedMs}ms", message);
        Assert.Equal(expectColdStart, message.Contains("cold start"));
    }

    /// <summary>
    /// Builds a HealthCheckService wired only with the dependencies that
    /// <see cref="HealthCheckService.CheckMcpServerAsync"/> touches. The MCP base URL is
    /// pinned via the <c>McpServerUrl</c> override so the stub handler matches.
    /// </summary>
    private static HealthCheckService BuildService(HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler().When("/health", status, body);
        return BuildServiceWithFactory(new StubHttpClientFactory(handler));
    }

    private static HealthCheckService BuildServiceWithFactory(
        System.Net.Http.IHttpClientFactory factory,
        Dictionary<string, string?>? extraConfig = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["McpServerUrl"] = "https://mcp.example.test",
        };
        if (extraConfig != null)
        {
            foreach (var kv in extraConfig) settings[kv.Key] = kv.Value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new HealthCheckService(
            NullLogger<HealthCheckService>.Instance,
            adminConfigService: null!,
            httpClientFactory: factory,
            metricsReader: null!,
            poisonQueueProbe: null!,
            configuration: config);
    }

    /// <summary>HTTP handler that always throws — simulates a cold-start timeout / connection failure.</summary>
    private sealed class ThrowingHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Exception _ex;
        public ThrowingHttpMessageHandler(System.Exception ex) => _ex = ex;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => throw _ex;
    }

    private sealed class ThrowingHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        private readonly System.Exception _ex;
        public ThrowingHttpClientFactory(System.Exception ex) => _ex = ex;
        public System.Net.Http.HttpClient CreateClient(string name)
            => new(new ThrowingHttpMessageHandler(_ex), disposeHandler: true);
    }
}
