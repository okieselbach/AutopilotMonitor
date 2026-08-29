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
        var alertDispatch = new OpsAlertDispatchService(
            adminConfig.Object,
            new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(),
                NullLogger<TelegramNotificationService>.Instance),
            new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance),
            NullLogger<OpsAlertDispatchService>.Instance);
        return (new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch), saved);
    }

    private static DiagnosticsUploadConfigChange EnableFlip() =>
        DiagnosticsUploadConfigChange.Detect(
            new TenantConfiguration { TenantId = "t1", DiagnosticsUploadMode = "Off" },
            new TenantConfiguration { TenantId = "t1", DiagnosticsUploadMode = "OnFailure", DiagnosticsUploadDestination = "Hosted" })!;

    [Theory]
    [InlineData("collect-logs", UpdateTenantConfigurationFunction.CollectLogsSource)]
    [InlineData("COLLECT-LOGS", UpdateTenantConfigurationFunction.CollectLogsSource)]
    [InlineData(null, "portal-put")]
    [InlineData("", "portal-put")]
    [InlineData("something-else", "portal-put")]
    public void ResolveWriteSource_only_honours_the_allow_listed_intent(string? intent, string expected)
    {
        Assert.Equal(expected, UpdateTenantConfigurationFunction.ResolveWriteSource(intent));
    }

    [Fact]
    public async Task Quick_config_enable_lands_as_its_own_event_type()
    {
        var (service, saved) = Rig();

        await service.RecordDiagnosticsUploadConfigChangedAsync(
            "t1", "contoso.com", EnableFlip(), "admin@contoso.com", UpdateTenantConfigurationFunction.CollectLogsSource);

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

        await service.RecordDiagnosticsUploadConfigChangedAsync("t1", null, disable, "ga@x", UpdateTenantConfigurationFunction.CollectLogsSource);

        Assert.Equal("DiagnosticsUploadDisabled", Assert.Single(saved).EventType);
    }
}
