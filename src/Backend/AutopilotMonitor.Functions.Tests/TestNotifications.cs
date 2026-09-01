using System.Net.Http;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Inert notification plumbing for tests that need an <see cref="OpsEventService"/> but do not
/// assert on anything being sent. Nothing leaves the process: the webhook transport gets a bare
/// HttpClient it never uses (no channel is configured in these stubs), and the Telegram transport
/// resolves its bot URL from a mocked repository that returns nothing.
/// <para>
/// Tests that DO assert on dispatch should mock the transports directly — see
/// <c>TelegramChannelProviderTests</c> — rather than extending this helper.
/// </para>
/// </summary>
internal static class TestNotifications
{
    internal static NotificationChannelDispatcher InertDispatcher()
        => new(
            new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance),
            new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(),
                NullLogger<TelegramNotificationService>.Instance));

    internal static OpsAlertDispatchService InertOpsAlertDispatch(AdminConfigurationService adminConfigService)
        => new(adminConfigService, InertDispatcher(), NullLogger<OpsAlertDispatchService>.Instance);
}
