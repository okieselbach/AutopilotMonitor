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
/// Service-level tests for the Agent Binaries HealthCheck card. The check probes the
/// canonical download alias (download.autopilotmonitor.com) AND the legacy blob keepalive
/// account: alias failure is unhealthy (new installs break), legacy failure is only a
/// warning (existing customers break until they migrate to the alias).
/// </summary>
public class HealthCheckServiceAgentBinariesTests
{
    private const string AliasHost = "download.autopilotmonitor.com";
    private const string LegacyHost = "autopilotmonitor.blob.core.windows.net";

    [Fact]
    public async Task Check_AllEndpointsAvailable_ReportsHealthyWithoutUrlDetails()
    {
        var handler = new StubHttpMessageHandler()
            .When(AliasHost, HttpStatusCode.OK, "")
            .When(LegacyHost, HttpStatusCode.OK, "");

        var check = await BuildService(handler).CheckAgentBinariesAsync(includeEndpointUrls: false);

        Assert.Equal("Agent Binaries", check.Name);
        Assert.Equal("healthy", check.Status);
        Assert.Contains("download alias", check.Message);
        Assert.Null(check.Details);
        // Both hosts must actually have been probed (ZIP + PS1 each).
        Assert.Equal(2, handler.Requests.FindAll(u => u.Contains(AliasHost)).Count);
        Assert.Equal(2, handler.Requests.FindAll(u => u.Contains(LegacyHost)).Count);
    }

    [Fact]
    public async Task Check_WithEndpointUrls_SurfacesAliasAndLegacyUrlsInDetails()
    {
        var handler = new StubHttpMessageHandler()
            .When(AliasHost, HttpStatusCode.OK, "")
            .When(LegacyHost, HttpStatusCode.OK, "");

        var check = await BuildService(handler).CheckAgentBinariesAsync(includeEndpointUrls: true);

        Assert.NotNull(check.Details);
        Assert.Equal("https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip", check.Details!["Agent ZIP"]);
        Assert.Equal("https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor.ps1", check.Details["Bootstrap script"]);
        Assert.Equal("https://autopilotmonitor.blob.core.windows.net/agent", check.Details["Legacy blob (keepalive)"]);
    }

    [Fact]
    public async Task Check_LegacyBlobMissing_ReportsWarningNamingTheLegacyEndpoint()
    {
        var handler = new StubHttpMessageHandler()
            .When(AliasHost, HttpStatusCode.OK, "")
            .When(LegacyHost, HttpStatusCode.NotFound, "");

        var check = await BuildService(handler).CheckAgentBinariesAsync(includeEndpointUrls: false);

        Assert.Equal("warning", check.Status);
        Assert.Contains("legacy", check.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("404", check.Message);
    }

    [Fact]
    public async Task Check_AliasMissing_ReportsUnhealthy()
    {
        var handler = new StubHttpMessageHandler()
            .When(AliasHost, HttpStatusCode.NotFound, "")
            .When(LegacyHost, HttpStatusCode.OK, "");

        var check = await BuildService(handler).CheckAgentBinariesAsync(includeEndpointUrls: false);

        Assert.Equal("unhealthy", check.Status);
        Assert.Contains("download alias", check.Message);
    }

    [Fact]
    public async Task Check_EndpointsUnreachable_ReportsUnhealthy()
    {
        // Un-scripted URLs make the stub throw — models a connection failure.
        var check = await BuildService(new StubHttpMessageHandler()).CheckAgentBinariesAsync(includeEndpointUrls: false);

        Assert.Equal("unhealthy", check.Status);
        Assert.Contains("unreachable", check.Message);
    }

    private static HealthCheckService BuildService(StubHttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new HealthCheckService(
            NullLogger<HealthCheckService>.Instance,
            adminConfigService: null!,
            httpClientFactory: new StubHttpClientFactory(handler),
            metricsReader: null!,
            poisonQueueProbe: null!,
            configuration: config);
    }
}
