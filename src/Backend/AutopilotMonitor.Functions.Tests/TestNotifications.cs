using System.Net.Http;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
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
}
