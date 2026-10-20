using System.Net.Http;
using AutopilotMonitor.Functions.Functions.Raw;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Telemetry;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// Push as a channel provider: the dispatcher's Push branch (scope passed through, never the
/// webhook path, one fan-out per send), the validation rules (no destination, open to tenant
/// admins — only Telegram and E-mail stay behind the GA gate — and one Push channel per scope),
/// the raw-table deny-list and the dependency redaction.
/// </summary>
public class PushChannelRoutingTests
{
    private const string TenantKey = "11111111-1111-1111-1111-111111111111";
    private const string OnePushChannelOnly = "only one Push channel is allowed — a single channel already reaches every paired device.";
    private static readonly NotificationScope Tenant = NotificationScope.Tenant(TenantKey);

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

    private static NotificationChannel PushChannel(string id = "push", bool enabled = true) =>
        new() { Id = id, Name = id, ProviderType = (int)WebhookProviderType.Push, Url = null, Enabled = enabled };

    private static string Json(params NotificationChannel[] channels) => NotificationChannel.SerializeList(channels);

    private static TenantConfiguration Config(params NotificationChannel[] channels)
        => new() { TenantId = TenantKey, NotificationChannelsJson = Json(channels) };

    [Fact]
    public async Task Push_channel_reaches_the_push_sender_with_the_callers_scope_and_never_the_webhook_path()
    {
        var webhook = WebhookMock();
        var telegram = TelegramMock();
        var push = new Mock<IPushChannelSender>();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, telegram.Object, push.Object);
        var alert = new NotificationAlert { Title = "t", Summary = "s" };

        await dispatcher.SendToChannelsAsync(new[] { PushChannel() }, alert, Tenant);

        push.Verify(p => p.SendAsync(Tenant, alert), Times.Once);
        webhook.Verify(w => w.SendAsync(It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Never);
        telegram.Verify(t => t.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()), Times.Never);
    }

    [Fact]
    public async Task Two_push_channels_in_one_send_fan_out_once()
    {
        // A pair stored before validation allowed only one: each would reach every paired device.
        var webhook = WebhookMock();
        var push = new Mock<IPushChannelSender>();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, TelegramMock().Object, push.Object);
        var alert = new NotificationAlert { Title = "t", Summary = "s" };
        var slack = new NotificationChannel { Id = "slack", Name = "slack", ProviderType = (int)WebhookProviderType.Slack, Url = "https://hooks.slack.example/x", Enabled = true };

        await dispatcher.SendToChannelsAsync(new[] { PushChannel("phones"), slack, PushChannel("phones-2") }, alert, Tenant);

        push.Verify(p => p.SendAsync(Tenant, alert), Times.Once);
        push.VerifyNoOtherCalls();
        // The other channels of the send are untouched by the guard.
        webhook.Verify(w => w.SendAsync("https://hooks.slack.example/x", WebhookProviderType.Slack, alert,
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Push_channel_without_url_is_not_skipped_as_destination_less()
    {
        var push = new Mock<IPushChannelSender>();
        push.Setup(p => p.SendWithResultAsync(It.IsAny<NotificationScope>(), It.IsAny<NotificationAlert>()))
            .ReturnsAsync(new NotificationSendResult { Success = true, Message = "ok" });
        var dispatcher = new NotificationChannelDispatcher(WebhookMock().Object, TelegramMock().Object, push.Object);

        var result = await dispatcher.SendTestAsync(PushChannel(), new NotificationAlert { Title = "t", Summary = "s" }, NotificationScope.Platform);

        Assert.True(result.Success);
        push.Verify(p => p.SendWithResultAsync(NotificationScope.Platform, It.IsAny<NotificationAlert>()), Times.Once);
    }

    [Fact]
    public void HasDestination_is_the_one_rule()
    {
        Assert.True(PushChannel().HasDestination());
        Assert.False(new NotificationChannel { Id = "s", ProviderType = (int)WebhookProviderType.Slack, Url = "" }.HasDestination());
        Assert.True(new NotificationChannel { Id = "s", ProviderType = (int)WebhookProviderType.Slack, Url = "https://hooks.slack.example/x" }.HasDestination());
        Assert.False(NotificationChannel.IsGlobalAdminOnlyProvider((int)WebhookProviderType.Push));
        Assert.True(NotificationChannel.IsGlobalAdminOnlyProvider((int)WebhookProviderType.Telegram));
        Assert.True(NotificationChannel.IsGlobalAdminOnlyProvider((int)WebhookProviderType.Email));
        Assert.False(NotificationChannel.IsGlobalAdminOnlyProvider((int)WebhookProviderType.Slack));
    }

    [Fact]
    public void Validation_accepts_a_push_channel_without_url_and_refuses_one_with_a_url()
    {
        Assert.Null(TenantConfigValidation.ValidateNotificationChannels(NotificationChannel.SerializeList(new[] { PushChannel() })));

        var withUrl = PushChannel();
        withUrl.Url = "https://attacker.invalid/hook";
        var error = TenantConfigValidation.ValidateNotificationChannels(NotificationChannel.SerializeList(new[] { withUrl }));
        Assert.NotNull(error);
        Assert.Contains("no destination", error);
    }

    [Fact]
    public void Gate_leaves_a_push_channel_to_the_tenant_admin_but_not_its_change_into_telegram()
    {
        var json = Json(PushChannel());

        // Add, disable, rename and toggle: all the tenant admin's own.
        Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(json, existingJson: null, isGlobalAdmin: false));
        Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(Json(PushChannel(enabled: false)), json, isGlobalAdmin: false));
        var renamed = PushChannel();
        renamed.Name = "Phones";
        renamed.NotifyOnFailure = true;
        Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(Json(renamed), json, isGlobalAdmin: false));

        // The same channel id turned into Telegram still needs a Global Administrator.
        var retyped = new NotificationChannel { Id = "push", Name = "push", ProviderType = (int)WebhookProviderType.Telegram, Url = "-100123", Enabled = true };
        Assert.Equal("Telegram channels can only be configured by a Global Administrator.",
            TenantConfigValidation.ValidateTelegramChannelGate(Json(retyped), json, isGlobalAdmin: false));
        Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(Json(retyped), json, isGlobalAdmin: true));
    }

    [Fact]
    public void A_tenant_admin_may_add_the_first_push_channel()
        => Assert.Null(TenantConfigValidation.ValidateModel(Config(PushChannel()), Config(), isGlobalAdmin: false));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_second_push_channel_is_refused_for_every_caller(bool isGlobalAdmin)
    {
        var error = TenantConfigValidation.ValidateModel(
            Config(PushChannel("phones"), PushChannel("phones-2")), Config(PushChannel("phones")), isGlobalAdmin);

        Assert.Equal("Invalid notification channels: " + OnePushChannelOnly, error);
    }

    [Fact]
    public void A_disabled_push_channel_counts_toward_the_one()
        => Assert.Equal(OnePushChannelOnly,
            TenantConfigValidation.ValidateSinglePushChannel(Json(PushChannel("phones"), PushChannel("spare", enabled: false)), existingJson: null));

    [Fact]
    public void A_stored_pair_stays_saveable_until_it_is_reduced()
    {
        var pair = Json(PushChannel("phones"), PushChannel("phones-2"));

        // Saved before the rule: unrelated saves and the reduction pass, growing it does not.
        Assert.Null(TenantConfigValidation.ValidateSinglePushChannel(pair, pair));
        Assert.Null(TenantConfigValidation.ValidateModel(Config(PushChannel("phones"), PushChannel("phones-2")),
            Config(PushChannel("phones"), PushChannel("phones-2")), isGlobalAdmin: false));
        Assert.Null(TenantConfigValidation.ValidateSinglePushChannel(Json(PushChannel("phones")), pair));
        Assert.Equal(OnePushChannelOnly, TenantConfigValidation.ValidateSinglePushChannel(
            Json(PushChannel("phones"), PushChannel("phones-2"), PushChannel("phones-3")), pair));

        // Once reduced, the pair cannot come back.
        Assert.Equal(OnePushChannelOnly, TenantConfigValidation.ValidateSinglePushChannel(pair, Json(PushChannel("phones"))));
    }

    [Fact]
    public void Raw_table_surface_denies_every_credential_bearing_table()
    {
        foreach (var table in Constants.TableNames.CredentialBearing)
        {
            Assert.Contains(table, Constants.TableNames.All);
            Assert.Contains(table, TableQueryFunction._blacklistedTables);
        }
        Assert.Contains(Constants.TableNames.PushDevices, Constants.TableNames.CredentialBearing);
        Assert.Contains(Constants.TableNames.PushPairingGrants, Constants.TableNames.CredentialBearing);
    }

    [Theory]
    [InlineData("https://web.push.apple.com/QF3abc/device-token-123", "https://web.push.apple.com")]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc:APA91", "https://fcm.googleapis.com")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAA", "https://updates.push.services.mozilla.com")]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", "https://wns2-db5p.notify.windows.com")]
    public void Dependency_rows_for_push_services_keep_scheme_and_host_only(string endpoint, string expected)
    {
        var dependency = new DependencyTelemetry { Data = endpoint, Name = "POST " + endpoint, Target = new Uri(endpoint).Host };

        PushDependencyRedactionProcessor.Redact(dependency);

        Assert.Equal(expected, dependency.Data);
        Assert.Equal("POST " + expected, dependency.Name);
    }

    [Fact]
    public void Dependency_rows_for_other_hosts_are_untouched()
    {
        var dependency = new DependencyTelemetry { Data = "https://graph.microsoft.com/v1.0/devices/abc", Name = "GET /v1.0/devices/abc" };

        PushDependencyRedactionProcessor.Redact(dependency);

        Assert.Equal("https://graph.microsoft.com/v1.0/devices/abc", dependency.Data);
        Assert.Equal("GET /v1.0/devices/abc", dependency.Name);
    }
}
