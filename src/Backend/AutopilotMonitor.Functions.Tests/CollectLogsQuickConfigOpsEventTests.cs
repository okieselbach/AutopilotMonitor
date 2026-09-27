using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The targeted push signal: "someone clicked Collect Logs and switched on Hosted upload
/// through the quick-config dialog" must land as its own ops event type, while every other
/// enable path keeps the generic type — an alert rule filters by EventType only.
/// </summary>
public class CollectLogsQuickConfigOpsEventTests
{
    private static (OpsEventService Service, List<OpsEventEntry> Saved) Rig()
    {
        var saved = new List<OpsEventEntry>();
        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => { lock (saved) saved.Add(e); })
            .Returns(Task.CompletedTask);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
        var alertDispatch = TestNotifications.InertOpsAlertDispatch(adminConfig.Object);
        return (new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch), saved);
    }

    private static DiagnosticsUploadConfigChange EnableFlip() =>
        DiagnosticsUploadConfigChange.Detect(
            new TenantConfiguration { TenantId = "t1", DiagnosticsUploadMode = "Off" },
            new TenantConfiguration { TenantId = "t1", DiagnosticsUploadMode = "OnFailure", DiagnosticsUploadDestination = "Hosted" })!;

    [Theory]
    [InlineData("?intent=collect-logs", "patch", PatchTenantConfigurationFieldsFunction.CollectLogsSource)]
    [InlineData("?intent=COLLECT-LOGS", "patch", PatchTenantConfigurationFieldsFunction.CollectLogsSource)]
    [InlineData("", "patch", "api-patch")]
    [InlineData("?intent=", "patch", "api-patch")]
    [InlineData("?intent=something-else", "patch", "api-patch")]
    [InlineData("?intent=collect-logs", "revert", "api-revert")] // a revert is never the quick-config dialog
    public void ResolveSource_only_honours_the_allow_listed_intent(string query, string operation, string expected)
    {
        var (req, _) = EndpointHarness.Request("t1", queryString: query);
        Assert.Equal(expected, PatchTenantConfigurationFieldsFunction.ResolveSource(req, operation));
    }

    [Fact]
    public void ResolveSource_mcp_header_without_intent_is_mcp()
    {
        var (req, _) = EndpointHarness.Request("t1", headers: new Dictionary<string, string> { ["X-Client-Source"] = "mcp" });
        Assert.Equal("mcp-patch", PatchTenantConfigurationFieldsFunction.ResolveSource(req, "patch"));
    }

    [Fact]
    public async Task Quick_config_enable_lands_as_its_own_event_type()
    {
        var (service, saved) = Rig();

        await service.RecordDiagnosticsUploadConfigChangedAsync(
            "t1", "contoso.com", EnableFlip(), "admin@contoso.com", PatchTenantConfigurationFieldsFunction.CollectLogsSource);

        var evt = Assert.Single(saved);
        Assert.Equal("CollectLogsQuickConfigEnabled", evt.EventType);
        Assert.Equal(OpsEventCategory.Tenant, evt.Category);
        Assert.Contains("admin@contoso.com", evt.Message);
        Assert.Contains("Hosted", evt.Message);
    }

    [Theory]
    [InlineData("portal-put")]
    [InlineData("mcp-patch")]
    public async Task Other_enable_paths_keep_the_generic_event_type(string source)
    {
        var (service, saved) = Rig();

        await service.RecordDiagnosticsUploadConfigChangedAsync("t1", "contoso.com", EnableFlip(), "admin@contoso.com", source);

        Assert.Equal("DiagnosticsUploadEnabled", Assert.Single(saved).EventType);
    }

    [Fact]
    public async Task Quick_config_source_on_a_disable_is_the_generic_disabled_type()
    {
        var (service, saved) = Rig();
        var disable = DiagnosticsUploadConfigChange.Detect(
            new TenantConfiguration { TenantId = "t1", DiagnosticsUploadMode = "Always", DiagnosticsUploadDestination = "Hosted" },
            new TenantConfiguration { TenantId = "t1", DiagnosticsUploadMode = "Off", DiagnosticsUploadDestination = "Hosted" })!;

        await service.RecordDiagnosticsUploadConfigChangedAsync("t1", null, disable, "ga@x", PatchTenantConfigurationFieldsFunction.CollectLogsSource);

        Assert.Equal("DiagnosticsUploadDisabled", Assert.Single(saved).EventType);
    }
}
