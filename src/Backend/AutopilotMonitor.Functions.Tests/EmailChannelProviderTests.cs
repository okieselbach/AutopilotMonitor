using System.Net;
using System.Net.Http;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// E-mail as a notification-channel provider (D-332): dispatch routing (never the webhook path),
/// recipient validation, the Global-Admin gate, the rendered message, and the
/// send against a stubbed provider endpoint.
/// </summary>
public class EmailChannelProviderTests
{
    private const int Email = (int)WebhookProviderType.Email;
    private static readonly NotificationScope Platform = NotificationScope.Platform;

    private static Mock<WebhookNotificationService> WebhookMock()
    {
        var mock = new Mock<WebhookNotificationService>(new HttpClient()) { CallBase = false };
        mock.Setup(w => w.SendAsync(It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()))
            .ReturnsAsync(new NotificationSendResult { Success = true });
        return mock;
    }

    private static Mock<IEmailChannelSender> EmailMock()
    {
        var mock = new Mock<IEmailChannelSender>();
        mock.Setup(e => e.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()))
            .ReturnsAsync(new NotificationSendResult { Success = true });
        return mock;
    }

    private static NotificationChannel Channel(string? url, string id = "mail", bool enabled = true)
        => new() { Id = id, Name = id, ProviderType = Email, Url = url, Enabled = enabled };

    private static NotificationChannel Webhook(WebhookProviderType provider, string url)
        => new() { Id = provider.ToString(), Name = provider.ToString(), ProviderType = (int)provider, Url = url, Enabled = true };

    private static NotificationAlert Alert() => new()
    {
        EventType = "PushDeliveryFailed",
        Title = "Ops Alert: Platform/PushDeliveryFailed",
        Summary = "A push service refused the platform key",
        Severity = NotificationSeverity.Error,
        Facts = { new() { Name = "Category", Value = "Platform" }, new() { Name = "Tenant", Value = "11111111-1111-1111-1111-111111111111" } },
        Sections = { new() { Title = "What happened", Text = "status 401 <kid=abc>" } },
        Actions = { new() { Type = "openUrl", Title = "Open alerts", Url = "https://portal.example.invalid/admin/settings/alerts" } },
    };

    // ── Dispatch routing ──────────────────────────────────────────────────

    [Fact]
    public async Task Email_channel_reaches_the_email_sender_with_its_recipients_and_never_the_webhook_path()
    {
        var webhook = WebhookMock();
        var email = EmailMock();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, Mock.Of<IPushChannelSender>(), email.Object);
        var alert = Alert();

        await dispatcher.SendToChannelsAsync(new[] { Channel("ops@example.invalid; two@example.invalid") }, alert, Platform);

        email.Verify(e => e.SendAlertAsync("ops@example.invalid; two@example.invalid", alert), Times.Once);
        webhook.Verify(w => w.SendAsync(It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Email_channel_without_recipients_is_skipped_and_the_test_path_reports_the_sender_result()
    {
        var email = EmailMock();
        email.Setup(e => e.SendAlertAsync("ops@example.invalid", It.IsAny<NotificationAlert>()))
            .ReturnsAsync(new NotificationSendResult { Success = true, Message = "Sent to 1 recipient(s)." });
        var dispatcher = new NotificationChannelDispatcher(WebhookMock().Object, Mock.Of<IPushChannelSender>(), email.Object);

        await dispatcher.SendToChannelsAsync(new[] { Channel(""), Channel(null, "n") }, Alert(), Platform);
        email.Verify(e => e.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()), Times.Never);

        var result = await dispatcher.SendTestAsync(Channel("ops@example.invalid"), Alert(), Platform);
        Assert.True(result.Success);
        Assert.Equal("Sent to 1 recipient(s).", result.Message);
    }

    [Fact]
    public async Task Dispatcher_without_an_email_transport_skips_the_channel_and_says_so_on_test()
    {
        var dispatcher = new NotificationChannelDispatcher(WebhookMock().Object, Mock.Of<IPushChannelSender>());

        await dispatcher.SendToChannelsAsync(new[] { Channel("ops@example.invalid") }, Alert(), Platform);   // no throw
        var result = await dispatcher.SendTestAsync(Channel("ops@example.invalid"), Alert(), Platform);

        Assert.False(result.Success);
        Assert.Contains("not registered", result.Message);
    }

    [Fact]
    public async Task Webhook_channel_reaches_the_webhook_path_and_never_the_email_sender()
    {
        var webhook = WebhookMock();
        var email = EmailMock();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, Mock.Of<IPushChannelSender>(), email.Object);
        var alert = Alert();

        await dispatcher.SendToChannelsAsync(new[] { Webhook(WebhookProviderType.Slack, "https://hooks.slack.example/x") }, alert, Platform);

        webhook.Verify(w => w.SendAsync("https://hooks.slack.example/x", WebhookProviderType.Slack, alert,
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Once);
        email.Verify(e => e.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()), Times.Never);
    }

    [Fact]
    public async Task Mixed_list_each_channel_reaches_its_own_transport()
    {
        var webhook = WebhookMock();
        var email = EmailMock();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, Mock.Of<IPushChannelSender>(), email.Object);
        var alert = Alert();

        await dispatcher.SendToChannelsAsync(new[]
        {
            Channel("ops@example.invalid"),
            Webhook(WebhookProviderType.GenericJson, "https://sales.example/hook"),
        }, alert, Platform);

        email.Verify(e => e.SendAlertAsync("ops@example.invalid", alert), Times.Once);
        webhook.Verify(w => w.SendAsync("https://sales.example/hook", WebhookProviderType.GenericJson, alert,
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Channels_without_a_destination_are_skipped_on_every_transport()
    {
        var webhook = WebhookMock();
        var email = EmailMock();
        var dispatcher = new NotificationChannelDispatcher(webhook.Object, Mock.Of<IPushChannelSender>(), email.Object);

        await dispatcher.SendToChannelsAsync(new[] { Channel(null), Webhook(WebhookProviderType.Slack, "") }, Alert(), Platform);

        email.Verify(e => e.SendAlertAsync(It.IsAny<string>(), It.IsAny<NotificationAlert>()), Times.Never);
        webhook.Verify(w => w.SendAsync(It.IsAny<string>(), It.IsAny<WebhookProviderType>(), It.IsAny<NotificationAlert>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>()), Times.Never);
    }

    // ── Validation and gate ───────────────────────────────────────────────

    [Theory]
    [InlineData("ops@example.invalid")]
    [InlineData("ops@example.invalid; second@example.invalid")]
    [InlineData("ops@example.invalid, second@example.invalid ,third@sub.example.invalid")]
    [InlineData("  first.last+tag@example.invalid  ")]
    public void ValidateEmailDestination_accepts_one_to_five_plain_addresses(string recipients)
        => Assert.Null(TenantConfigValidation.ValidateEmailDestination(recipients));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(";,")]
    [InlineData("not-an-address")]
    [InlineData("Ops Team <ops@example.invalid>")]
    [InlineData("ops@localhost")]
    [InlineData("https://hooks.example.invalid/x")]
    [InlineData("a@x.invalid;b@x.invalid;c@x.invalid;d@x.invalid;e@x.invalid;f@x.invalid")]
    public void ValidateEmailDestination_rejects_blank_malformed_named_hostless_and_too_many(string? recipients)
        => Assert.NotNull(TenantConfigValidation.ValidateEmailDestination(recipients));

    [Fact]
    public void ValidateNotificationChannels_uses_the_recipient_rule_not_the_ssrf_url_gate()
    {
        var ok = NotificationChannel.SerializeList(new[] { Channel("ops@example.invalid") });
        Assert.Null(TenantConfigValidation.ValidateNotificationChannels(ok));

        var bad = NotificationChannel.SerializeList(new[] { Channel("https://hooks.example.invalid/x") });
        Assert.Contains("not a valid e-mail address", TenantConfigValidation.ValidateNotificationChannels(bad));

        // The branch is per provider: a recipient list on a webhook channel still meets the URL gate.
        var onWebhook = NotificationChannel.SerializeList(new[] { Webhook(WebhookProviderType.Slack, "ops@example.invalid") });
        Assert.NotNull(TenantConfigValidation.ValidateNotificationChannels(onWebhook));
    }

    [Fact]
    public void Email_provider_is_global_admin_only()
    {
        Assert.True(NotificationChannel.IsGlobalAdminOnlyProvider(Email));
        var candidate = NotificationChannel.SerializeList(new[] { Channel("ops@example.invalid") });

        Assert.Null(TenantConfigValidation.ValidatePlatformProviderGate(candidate, null, isGlobalAdmin: true));
        var refused = TenantConfigValidation.ValidatePlatformProviderGate(candidate, null, isGlobalAdmin: false);
        Assert.Equal("E-mail channels can only be configured by a Global Administrator.", refused);

        // A GA-created channel survives a tenant admin's unrelated save; retargeting it does not.
        Assert.Null(TenantConfigValidation.ValidatePlatformProviderGate(candidate, candidate, isGlobalAdmin: false));
        var retargeted = NotificationChannel.SerializeList(new[] { Channel("other@example.invalid") });
        Assert.NotNull(TenantConfigValidation.ValidatePlatformProviderGate(retargeted, candidate, isGlobalAdmin: false));
    }

    [Fact]
    public void Gate_refuses_a_tenant_admin_who_flips_the_enabled_state()
    {
        var existing = NotificationChannel.SerializeList(new[] { Channel("ops@example.invalid", enabled: true) });
        var disabled = NotificationChannel.SerializeList(new[] { Channel("ops@example.invalid", enabled: false) });

        Assert.NotNull(TenantConfigValidation.ValidatePlatformProviderGate(disabled, existing, isGlobalAdmin: false));
    }

    [Fact]
    public void Gate_ignores_webhook_channels()
    {
        var candidate = NotificationChannel.SerializeList(new[] { Webhook(WebhookProviderType.GenericJson, "https://sales.example/hook") });

        Assert.Null(TenantConfigValidation.ValidatePlatformProviderGate(candidate, null, isGlobalAdmin: false));
    }

    [Fact]
    public void ValidateModel_enforces_the_gate_for_every_client_write()
    {
        var candidate = new TenantConfiguration
        {
            TenantId = "t1",
            NotificationChannelsJson = NotificationChannel.SerializeList(new[] { Channel("ops@example.invalid") }),
        };
        var existing = new TenantConfiguration { TenantId = "t1" };

        Assert.NotNull(TenantConfigValidation.ValidateModel(candidate, existing, isGlobalAdmin: false));
        Assert.Null(TenantConfigValidation.ValidateModel(candidate, existing, isGlobalAdmin: true));
    }

    // ── Rendering ─────────────────────────────────────────────────────────

    [Fact]
    public void Renderer_reads_like_a_message_not_a_log_line()
    {
        var alert = Alert();
        alert.DataJson = "{\"service\":\"apple\",\"statusCode\":401,\"note\":\"<script>\",\"nested\":{\"a\":1}}";

        // No brackets, no severity shouting, the event name as words, the category in parentheses.
        Assert.Equal("Autopilot Monitor: Push delivery failed (Platform)", EmailAlertRenderer.Subject(alert));

        var html = EmailAlertRenderer.Html(alert);
        Assert.Contains("Autopilot Monitor raised an error in the Platform category.", html);   // one sentence first
        Assert.Contains("<h1 style=\"font-size:18px;margin:0 0 8px 0;\">Push delivery failed</h1>", html);
        Assert.DoesNotContain("Ops Alert: Platform/PushDeliveryFailed", html);                    // the machine title stays out
        Assert.Contains("A push service refused the platform key", html);
        Assert.Contains("<td style=\"padding:3px 0;\">Platform</td>", html);                     // facts as a table
        Assert.Contains("status 401 &lt;kid=abc&gt;", html);                                        // section text escaped
        Assert.DoesNotContain("<kid=abc>", html);
        Assert.Contains("<td style=\"padding:3px 0;\">&lt;script&gt;</td>", html);               // payload as rows, escaped
        Assert.Contains("<td style=\"padding:3px 0;\">{&quot;a&quot;:1}</td>", html);             // nested values stay compact JSON
        Assert.DoesNotContain("<pre", html);                                                        // no raw JSON block for an object
        Assert.Contains("Open alerts: <a href=\"https://portal.example.invalid/admin/settings/alerts\"", html);   // visible link
        Assert.Contains("sent automatically by Autopilot Monitor", html);

        var text = EmailAlertRenderer.Text(alert);
        Assert.StartsWith("Autopilot Monitor raised an error in the Platform category.", text);
        Assert.Contains("Push delivery failed\n", text);
        Assert.Contains("Category: Platform\n", text);
        Assert.Contains("note: <script>\n", text);                                                 // text part is not HTML-escaped
        Assert.Contains("Open alerts: https://portal.example.invalid/admin/settings/alerts", text);
    }

    [Fact]
    public void Renderer_drops_non_http_links_caps_a_non_object_payload_and_survives_an_empty_alert()
    {
        var alert = new NotificationAlert { Title = "", Summary = "", DataJson = new string('x', EmailAlertRenderer.MaxPayloadChars + 100) };
        alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "bad", Url = "javascript:alert(1)" });

        Assert.Equal("Autopilot Monitor: Notification", EmailAlertRenderer.Subject(alert));
        var html = EmailAlertRenderer.Html(alert);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("(truncated)", html);
        Assert.True(html.Length < EmailAlertRenderer.MaxPayloadChars + 3000);
        Assert.Equal("Autopilot Monitor: Hello world", EmailAlertRenderer.Subject(new NotificationAlert { Title = "Hello world", Summary = "" }));
    }

    [Theory]
    [InlineData("PushDeliveryFailed", "Push delivery failed")]
    [InlineData("SlaBreachNotification", "Sla breach notification")]
    [InlineData("SLAEvaluation", "SLA evaluation")]
    [InlineData("enrollment_failed", "Enrollment failed")]
    [InlineData("TenantSignup", "Tenant signup")]
    public void Humanize_splits_event_names_into_words(string eventName, string expected)
        => Assert.Equal(expected, EmailAlertRenderer.Humanize(eventName));

    // ── Sending through the provider path ─────────────────────────────────

    [Fact]
    public async Task Sender_posts_one_message_per_recipient_and_reports_what_the_provider_accepted()
    {
        var handler = new StubHandler();
        var (service, _) = Sender(handler, apiKey: "key");

        var result = await service.SendAlertAsync("ops@example.invalid; second@example.invalid", Alert());

        Assert.True(result.Success);
        Assert.Equal("Sent to 2 recipient(s). Provider status: sent, id abc,abc.", result.Message);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"to\":[{\"email\":\"ops@example.invalid\"", handler.Bodies[0]);
        Assert.Contains("\"subject\":\"Autopilot Monitor: Push delivery failed (Platform)\"", handler.Bodies[0]);
        Assert.Contains("\"tags\":[\"alert\"]", handler.Bodies[0]);
        Assert.Contains("\"track_opens\":false", handler.Bodies[0]);
        Assert.Contains("\"text\":\"Autopilot Monitor raised an error", handler.Bodies[0]);      // own text part
        Assert.Contains("\"auto_text\":false", handler.Bodies[0]);
        Assert.Contains("\"headers\":{\"Auto-Submitted\":\"auto-generated\",\"X-Auto-Response-Suppress\":\"All\"}", handler.Bodies[0]);
    }

    [Fact]
    public async Task Sender_reports_a_rejection_and_an_unconfigured_provider_without_throwing()
    {
        var handler = new StubHandler { Responder = _ => StubHandler.Json(HttpStatusCode.OK, "[{\"email\":\"ops@example.invalid\",\"status\":\"rejected\",\"reject_reason\":\"hard-bounce\"}]") };
        var (service, _) = Sender(handler, apiKey: "key");
        var rejected = await service.SendAlertAsync("ops@example.invalid", Alert());
        Assert.False(rejected.Success);
        Assert.Contains("accepted 0 of 1", rejected.Message);
        Assert.Contains("Provider status: rejected", rejected.Message);

        var (unconfigured, _) = Sender(new StubHandler(), apiKey: "");
        var skipped = await unconfigured.SendAlertAsync("ops@example.invalid", Alert());
        Assert.False(skipped.Success);
        Assert.Contains("not configured", skipped.Message);

    }

    [Fact]
    public void ParseRecipients_trims_dedupes_and_keeps_order()
    {
        var list = EmailNotificationService.ParseRecipients(" a@x.invalid ; B@x.invalid, a@x.invalid ,, c@x.invalid ");
        Assert.Equal(new[] { "a@x.invalid", "B@x.invalid", "c@x.invalid" }, list);
        Assert.Empty(EmailNotificationService.ParseRecipients(null));
    }

    private static (EmailNotificationService Service, EmailService Email) Sender(StubHandler handler, string apiKey)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [EmailService.ApiKeyConfigKey] = apiKey,
            ["Email:Endpoint"] = "https://mail.example.invalid/send",
        }).Build();
        var templates = new EmailTemplateService(
            Mock.Of<IConfigRepository>(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            NullLogger<EmailTemplateService>.Instance);
        var email = new EmailService(new HttpClient(handler, disposeHandler: false), config, templates, NullLogger<EmailService>.Instance);
        return (new EmailNotificationService(email, NullLogger<EmailNotificationService>.Instance), email);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ =>
            Json(HttpStatusCode.OK, "[{\"email\":\"x@y.invalid\",\"status\":\"sent\",\"_id\":\"abc\"}]");

        public static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return Responder(request);
        }
    }
}
