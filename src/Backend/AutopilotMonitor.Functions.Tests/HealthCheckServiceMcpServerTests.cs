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
    public async Task Check_TimeoutOrConnectionFailure_ReportsWarningForColdStart()
    {
        var svc = BuildServiceWithFactory(
            new ThrowingHttpClientFactory(new TaskCanceledException("timeout")));

        var check = await svc.CheckMcpServerAsync();

        // Failure to wake the scaled-to-zero container within the budget is a warning,
        // not a hard outage (a re-check may still succeed once it finishes warming).
        Assert.Equal("warning", check.Status);
        Assert.Contains("could not be reached", check.Message);
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

    private static HealthCheckService BuildServiceWithFactory(System.Net.Http.IHttpClientFactory factory)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServerUrl"] = "https://mcp.example.test",
            })
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
