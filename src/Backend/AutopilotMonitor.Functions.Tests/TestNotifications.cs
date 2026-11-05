using System.Net.Http;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Inert notification plumbing for tests that need an <see cref="OpsEventService"/> but do not
/// assert on anything being sent. Nothing leaves the process: the webhook transport gets a bare
/// HttpClient it never uses (no channel is configured in these stubs).
/// <para>
/// Tests that DO assert on dispatch should mock the transports directly — see
/// <c>EmailChannelProviderTests</c> — rather than extending this helper.
/// </para>
/// </summary>
internal static class TestNotifications
{
    internal static NotificationChannelDispatcher InertDispatcher()
        => new(
            new WebhookNotificationService(new HttpClient()),
            Mock.Of<IPushChannelSender>());

    internal static OpsAlertDispatchService InertOpsAlertDispatch(AdminConfigurationService adminConfigService)
        => new(adminConfigService, InertDispatcher(), NullLogger<OpsAlertDispatchService>.Instance);

    /// <summary>An <see cref="OpsEventService"/> that records every saved event in <paramref name="sink"/> and sends nothing.</summary>
    internal static OpsEventService CapturingOpsEvents(List<OpsEventEntry> sink)
    {
        var repo = new Mock<IOpsEventRepository>();
        repo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Returns<OpsEventEntry>(e => { lock (sink) sink.Add(e); return Task.CompletedTask; });
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        return new OpsEventService(repo.Object, NullLogger<OpsEventService>.Instance, InertOpsAlertDispatch(adminConfig.Object));
    }
}
