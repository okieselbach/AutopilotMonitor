using System.Net.Http;
using AutopilotMonitor.Functions.Functions.Raw;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Telemetry;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// Push as a channel provider: the dispatcher's Push branch (scope passed through, never the
/// webhook path), the validation rules and the GA gate, the raw-table deny-list and the
/// dependency redaction.
/// </summary>
public class PushChannelRoutingTests
{
    private static readonly NotificationScope Tenant = NotificationScope.Tenant("11111111-1111-1111-1111-111111111111");

    private static Mock<WebhookNotificationService> WebhookMock()
    {
        var mock = new Mock<WebhookNotificationService>(new HttpClient(), NullLogger<WebhookNotificationService>.Instance) { CallBase = false };
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

    private static NotificationChannel PushChannel(string id = "push") =>
        new() { Id = id, Name = id, ProviderType = (int)WebhookProviderType.Push, Url = null, Enabled = true };

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
        Assert.True(NotificationChannel.IsGlobalAdminOnlyProvider((int)WebhookProviderType.Push));
        Assert.True(NotificationChannel.IsGlobalAdminOnlyProvider((int)WebhookProviderType.Telegram));
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
    public void Gate_lets_only_a_global_admin_add_or_flip_a_push_channel()
    {
        var json = NotificationChannel.SerializeList(new[] { PushChannel() });

        Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(json, existingJson: null, isGlobalAdmin: true));
        Assert.Contains("Push channels can only be configured by a Global Administrator",
            TenantConfigValidation.ValidateTelegramChannelGate(json, existingJson: null, isGlobalAdmin: false));

        // Unchanged GA-created channel: a tenant admin may still save unrelated fields.
        Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(json, json, isGlobalAdmin: false));

        var disabled = PushChannel();
        disabled.Enabled = false;
        Assert.NotNull(TenantConfigValidation.ValidateTelegramChannelGate(
            NotificationChannel.SerializeList(new[] { disabled }), json, isGlobalAdmin: false));

        // Renames and toggles stay with the tenant admin.
        var renamed = PushChannel();
        renamed.Name = "Phones";
        renamed.NotifyOnFailure = true;
        Assert.Null(TenantConfigValidation.ValidateTelegramChannelGate(
            NotificationChannel.SerializeList(new[] { renamed }), json, isGlobalAdmin: false));
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
