using System.Net.Http;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Telegram as a notification-channel provider: dispatch routing (it must never reach the
/// SSRF-guarded webhook path), chat-ID validation, and the Global-Admin gate that keeps a
/// tenant admin from sending through the platform-owned bot.
/// </summary>
public class TelegramChannelProviderTests
{
    private static Mock<WebhookNotificationService> WebhookMock()
    {
        var mock = new Mock<WebhookNotificationService>(new HttpClient()) { CallBase = false };
        mock.Setup(w => w.SendAsync(It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()))
            .ReturnsAsync(new NotificationSendResult { Success = true });
        return mock;
    }

    private static Mock<TelegramNotificationService> TelegramMock()
    {
        var mock = new Mock<TelegramNotificationService>(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance)
        { CallBase = false };
        mock.Setup(t => t.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()))
            .ReturnsAsync(new NotificationSendResult { Success = true });
        return mock;
    }

    private static NotificationChannel Channel(int providerType, string? url, string id = "c1", bool enabled = true)
        => new() { Id = id, Name = id, ProviderType = providerType, Url = url, Enabled = enabled };

    private static string ChannelJson(int providerType, string url, string id = "c1", bool enabled = true)
        => $"[{{\"id\":\"{id}\",\"name\":\"{id}\",\"providerType\":{providerType},\"url\":\"{url}\",\"enabled\":{(enabled ? "true" : "false")}}}]";

    // ── Dispatch routing ──────────────────────────────────────────────────

    [Fact]
    public async Task SendToChannels_RoutesTelegramToBotAndNeverToWebhook()
    {
        var webhook = WebhookMock();
        var telegram = TelegramMock();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, telegram.Object, Mock.Of<IPushChannelSender>());
        var alert = new NotificationAlert { Title = "t", Summary = "s" };

        await dispatcher.SendToChannelsAsync(
            new[] { Channel((int)WebhookProviderType.Telegram, "-1003785642894") }, alert, NotificationScope.Platform);

        telegram.Verify(t => t.SendAlertAsync("-1003785642894", alert), Times.Once);
        webhook.Verify(w => w.SendAsync(
            It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SendToChannels_RoutesWebhookProvidersToWebhookService()
    {
        var webhook = WebhookMock();
        var telegram = TelegramMock();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, telegram.Object, Mock.Of<IPushChannelSender>());
        var alert = new NotificationAlert { Title = "t", Summary = "s" };

        await dispatcher.SendToChannelsAsync(
            new[] { Channel((int)WebhookProviderType.Slack, "https://hooks.slack.example/x") }, alert, NotificationScope.Platform);

        webhook.Verify(w => w.SendAsync(
            "https://hooks.slack.example/x", WebhookProviderType.Slack, alert,
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Once);
        telegram.Verify(t => t.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()), Times.Never);
    }

    [Fact]
    public async Task SendToChannels_MixedList_EachChannelReachesItsOwnTransport()
    {
        var webhook = WebhookMock();
        var telegram = TelegramMock();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, telegram.Object, Mock.Of<IPushChannelSender>());
        var alert = new NotificationAlert { Title = "t", Summary = "s" };

        await dispatcher.SendToChannelsAsync(new[]
        {
            Channel((int)WebhookProviderType.Telegram, "-100123", "tg"),
            Channel((int)WebhookProviderType.GenericJson, "https://sales.example/hook", "sales"),
        }, alert, NotificationScope.Platform);

        telegram.Verify(t => t.SendAlertAsync("-100123", alert), Times.Once);
        webhook.Verify(w => w.SendAsync(
            "https://sales.example/hook", WebhookProviderType.GenericJson, alert,
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SendToChannels_SkipsChannelsWithoutDestination()
    {
        var webhook = WebhookMock();
        var telegram = TelegramMock();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, telegram.Object, Mock.Of<IPushChannelSender>());

        await dispatcher.SendToChannelsAsync(new[]
        {
            Channel((int)WebhookProviderType.Telegram, null, "tg"),
            Channel((int)WebhookProviderType.Slack, "", "slack"),
        }, new NotificationAlert { Title = "t", Summary = "s" }, NotificationScope.Platform);

        telegram.Verify(t => t.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()), Times.Never);
        webhook.Verify(w => w.SendAsync(
            It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SendTest_RoutesTelegramToBot()
    {
        var webhook = WebhookMock();
        var telegram = TelegramMock();
        telegram.Setup(t => t.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()))
            .ReturnsAsync(new NotificationSendResult { Success = true, Message = "ok" });
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, telegram.Object, Mock.Of<IPushChannelSender>());

        var result = await dispatcher.SendTestAsync(
            Channel((int)WebhookProviderType.Telegram, "@salesdesk"),
            new NotificationAlert { Title = "t", Summary = "s" }, NotificationScope.Platform);

        Assert.True(result.Success);
        telegram.Verify(t => t.SendAlertAsync("@salesdesk", It.IsAny<NotificationAlert>()), Times.Once);
    }

    // ── Chat-ID validation ────────────────────────────────────────────────

    [Theory]
    [InlineData("-1003785642894")]
    [InlineData("123456789")]
    [InlineData("@salesdesk")]
    [InlineData("@Sales_Desk_01")]
    public void ValidateTelegramChatId_AcceptsNumericAndUsernameForms(string chatId)
        => Assert.Null(TenantConfigValidation.ValidateTelegramChatId(chatId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://api.telegram.org/bot123/sendMessage")]
    [InlineData("-100abc")]
    [InlineData("@abc")]                       // too short
    [InlineData("@1sales")]                    // must start with a letter
    [InlineData("@sales-desk")]                // hyphen not allowed
    [InlineData("123456789012345678901")]      // 21 digits
    public void ValidateTelegramChatId_RejectsEverythingElse(string? chatId)
        => Assert.NotNull(TenantConfigValidation.ValidateTelegramChatId(chatId));

    [Fact]
    public void ValidateNotificationChannels_TelegramChatIdBypassesSsrfUrlGate()
    {
        // A chat ID is not a URL — the webhook SSRF gate would reject it. Proves the
        // provider-specific destination branch is wired, not just the format helper.
        Assert.Null(TenantConfigValidation.ValidateNotificationChannels(
            ChannelJson((int)WebhookProviderType.Telegram, "-1003785642894")));

        Assert.NotNull(TenantConfigValidation.ValidateNotificationChannels(
            ChannelJson((int)WebhookProviderType.Slack, "-1003785642894")));
    }

    [Fact]
    public void ValidateNotificationChannels_RejectsTelegramChannelWithUrlDestination()
        => Assert.NotNull(TenantConfigValidation.ValidateNotificationChannels(
            ChannelJson((int)WebhookProviderType.Telegram, "https://hooks.example/x")));

    // ── Global-Admin gate ─────────────────────────────────────────────────

    [Fact]
    public void TelegramGate_GlobalAdminMayAddChannel()
        => Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(
            ChannelJson((int)WebhookProviderType.Telegram, "-100123"), null, isGlobalAdmin: true));

    [Fact]
    public void TelegramGate_TenantAdminMayNotAddChannel()
        => Assert.NotNull(TenantConfigValidation.ValidateTelegramChannelGate(
            ChannelJson((int)WebhookProviderType.Telegram, "-100123"), null, isGlobalAdmin: false));

    [Fact]
    public void TelegramGate_TenantAdminMayNotRetargetExistingChannel()
    {
        var existing = ChannelJson((int)WebhookProviderType.Telegram, "-100123");
        var retargeted = ChannelJson((int)WebhookProviderType.Telegram, "-100999");

        Assert.NotNull(TenantConfigValidation.ValidateTelegramChannelGate(retargeted, existing, isGlobalAdmin: false));
    }

    [Fact]
    public void TelegramGate_TenantAdminMayNotFlipEnabledState()
    {
        var existing = ChannelJson((int)WebhookProviderType.Telegram, "-100123", enabled: true);
        var disabled = ChannelJson((int)WebhookProviderType.Telegram, "-100123", enabled: false);

        Assert.NotNull(TenantConfigValidation.ValidateTelegramChannelGate(disabled, existing, isGlobalAdmin: false));
    }

    [Fact]
    public void TelegramGate_TenantAdminMaySaveWithUnchangedGaCreatedChannel()
    {
        // The gate must not turn a GA-created Telegram channel into a permanent save block
        // for the tenant admin's unrelated configuration changes.
        var json = ChannelJson((int)WebhookProviderType.Telegram, "-100123");

        Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(json, json, isGlobalAdmin: false));
    }

    [Fact]
    public void TelegramGate_IgnoresNonTelegramChannels()
        => Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(
            ChannelJson((int)WebhookProviderType.GenericJson, "https://sales.example/hook"), null, isGlobalAdmin: false));

    // ── Both write paths (PUT + field patch) go through ValidateModel ─────

    [Fact]
    public void ValidateModel_EnforcesTelegramGateForNonGlobalAdmin()
    {
        var candidate = new TenantConfiguration
        {
            TenantId = "t1",
            NotificationChannelsJson = ChannelJson((int)WebhookProviderType.Telegram, "-100123"),
        };
        var existing = new TenantConfiguration { TenantId = "t1" };

        Assert.NotNull(TenantConfigValidation.ValidateModel(candidate, existing, isGlobalAdmin: false));
        Assert.Null(TenantConfigValidation.ValidateModel(candidate, existing, isGlobalAdmin: true));
    }
}
