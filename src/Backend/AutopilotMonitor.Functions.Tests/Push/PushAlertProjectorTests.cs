using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// The lock-screen projection (K23): what a push may carry, and — pinned with sentinels — what
/// it must never carry (Sections, DataJson, the Failure Reason fact, the ops message).
/// </summary>
public class PushAlertProjectorTests
{
    private const string Sentinel = "SENTINEL-MUST-NOT-LEAK";
    private static readonly NotificationScope Tenant = NotificationScope.Tenant("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static NotificationAlert EnrollmentFailed() => new()
    {
        EventType = "enrollment_failed",
        Title = "🔴 Enrollment Failed",
        Summary = "Enrollment Failed: DESKTOP-4711",
        Severity = NotificationSeverity.Error,
        Facts =
        {
            new() { Name = "Device", Value = "DESKTOP-4711" },
            new() { Name = "Serial", Value = "PF3ABCDE9" },
            new() { Name = "Hardware", Value = "LENOVO ThinkPad T14" },
            new() { Name = "Duration", Value = "41m 12s" },
            new() { Name = "Failure Reason", Value = Sentinel + " from the agent event message" },
        },
        Sections = { new() { Title = "Explanation", Text = Sentinel + " interpolated log line" } },
        Actions = { new() { Type = "openUrl", Title = "Open session", Url = Constants.PortalSessionUrl("sess-1") } },
        DataJson = "{\"secret\":\"" + Sentinel + "\"}",
    };

    [Fact]
    public void Tenant_projection_keeps_allow_listed_facts_and_masks_the_serial()
    {
        var payload = PushAlertProjector.Project(EnrollmentFailed(), Tenant, "e1", Now);

        Assert.Equal("enrollment_failed", payload.Type);
        Assert.Equal("🔴 Enrollment Failed", payload.Title);
        Assert.Equal("error", payload.Severity);
        Assert.Equal(Constants.PortalSessionUrl("sess-1"), payload.PortalUrl);
        Assert.Contains(("Device", "DESKTOP-4711"), payload.Facts);
        Assert.Contains(("Serial", "…CDE9"), payload.Facts);
        Assert.DoesNotContain(payload.Facts, f => f.Name == "Failure Reason");
    }

    [Fact]
    public void Nothing_outside_the_allow_list_reaches_the_wire()
    {
        var json = Encoding.UTF8.GetString(PushAlertProjector.ToJsonBytes(PushAlertProjector.Project(EnrollmentFailed(), Tenant, "e1", Now)));

        Assert.DoesNotContain(Sentinel, json);
        Assert.DoesNotContain("PF3ABCDE9", json);
    }

    [Fact]
    public void Consecutive_failures_alert_drops_the_last_failure_reason()
    {
        var alert = new NotificationAlert
        {
            EventType = "consecutive_failures",
            Title = "🔴 5 Consecutive Enrollment Failures",
            Summary = "Alert: 5 enrollments failed in a row for tenant 11111111-1111-1111-1111-111111111111",
            Severity = NotificationSeverity.Error,
            Facts =
            {
                new() { Name = "Consecutive Failures", Value = "5" },
                new() { Name = "Last Device", Value = "DESKTOP-0042" },
                new() { Name = "Last Failure", Value = Sentinel + " script output line" },
            },
        };

        var payload = PushAlertProjector.Project(alert, Tenant, "e5", Now);
        var json = Encoding.UTF8.GetString(PushAlertProjector.ToJsonBytes(payload));

        Assert.Contains(("Consecutive Failures", "5"), payload.Facts);
        Assert.DoesNotContain(payload.Facts, f => f.Name == "Last Failure");
        Assert.DoesNotContain(Sentinel, json);
    }

    [Fact]
    public void Platform_projection_never_uses_the_ops_message()
    {
        var alert = new NotificationAlert
        {
            EventType = "TenantAutoApproved",
            Title = "Ops Alert: Tenant/TenantAutoApproved",
            Summary = "signup by admin@contoso.invalid " + Sentinel,
            Severity = NotificationSeverity.Info,
            Facts =
            {
                new() { Name = "Category", Value = "Tenant" },
                new() { Name = "Event", Value = "TenantAutoApproved" },
                new() { Name = "Severity", Value = "Info" },
                new() { Name = "Contact Email", Value = "someone@contoso.invalid" },
            },
            DataJson = "{\"email\":\"someone@contoso.invalid\"}",
        };

        var payload = PushAlertProjector.Project(alert, NotificationScope.Platform, "e2", Now);
        var json = Encoding.UTF8.GetString(PushAlertProjector.ToJsonBytes(payload));

        Assert.Equal("Ops alert", payload.Title);
        Assert.Equal("Tenant/TenantAutoApproved · Info", payload.Body);
        Assert.DoesNotContain(Sentinel, json);
        Assert.DoesNotContain("contoso.invalid", json);
        Assert.Equal("platform", payload.Scope);
    }

    [Fact]
    public void Hardware_rejected_uses_the_fixed_template_not_the_distress_strings()
    {
        var alert = new NotificationAlert
        {
            EventType = "hardware_rejected",
            Title = "🟡 Hardware Not Whitelisted",
            Summary = "Device rejected: " + Sentinel + " is not in your hardware whitelist",
            Severity = NotificationSeverity.Warning,
            Facts =
            {
                new() { Name = "Manufacturer", Value = Sentinel },
                new() { Name = "Model", Value = Sentinel },
                new() { Name = "Serial", Value = "ZZ998877" },
            },
        };

        var payload = PushAlertProjector.Project(alert, Tenant, "e3", Now);
        var json = Encoding.UTF8.GetString(PushAlertProjector.ToJsonBytes(payload));

        Assert.Equal("Hardware rejected", payload.Title);
        Assert.Equal("Serial …8877 is not in the hardware whitelist.", payload.Body);
        Assert.DoesNotContain(Sentinel, json);
    }

    [Fact]
    public void Json_is_declarative_web_push_with_the_history_deep_link()
    {
        var payload = PushAlertProjector.Project(EnrollmentFailed(), Tenant, "entry-9", Now);
        using var doc = JsonDocument.Parse(PushAlertProjector.ToJsonBytes(payload));
        var root = doc.RootElement;

        Assert.Equal(8030, root.GetProperty("web_push").GetInt32());
        Assert.True(root.GetProperty("mutable").GetBoolean());
        var notification = root.GetProperty("notification");
        Assert.Equal(Constants.PortalPushHistoryUrl("entry-9"), notification.GetProperty("navigate").GetString());
        var data = notification.GetProperty("data");
        Assert.Equal(1, data.GetProperty("v").GetInt32());
        Assert.Equal("entry-9", data.GetProperty("id").GetString());
        Assert.Equal("enrollment_failed", data.GetProperty("type").GetString());
        Assert.Equal("tenant", data.GetProperty("scope").GetString());
    }

    [Fact]
    public void Over_budget_payloads_drop_facts_until_they_fit()
    {
        var alert = EnrollmentFailed();
        alert.Facts.Clear();
        for (var i = 0; i < 60; i++)
            alert.Facts.Add(new NotificationFact { Name = "Device", Value = new string('x', 60) + i });

        var bytes = PushAlertProjector.ToJsonBytes(PushAlertProjector.Project(alert, Tenant, "e4", Now));

        Assert.True(bytes.Length <= PushAlertProjector.MaxPayloadBytes, $"payload is {bytes.Length} bytes");
    }

    [Fact]
    public void Clean_strips_control_characters_and_caps_length()
    {
        Assert.Equal("a b", PushAlertProjector.Clean("a\u0000\u0007 \t b", 64));
        Assert.Equal(64, PushAlertProjector.Clean(new string('q', 200), 64).Length);
        Assert.Equal(string.Empty, PushAlertProjector.Clean("   ", 64));
    }

    [Fact]
    public void Topic_is_exactly_32_base64url_characters_and_opaque()
    {
        var topic = PushAlertProjector.BuildTopic(Constants.PortalSessionUrl("sess-1"), Encoding.UTF8.GetBytes("key"));

        Assert.NotNull(topic);
        Assert.Equal(32, topic!.Length);
        Assert.DoesNotContain("sess-1", topic);
        Assert.Null(PushAlertProjector.BuildTopic(Constants.PortalSessionUrl("sess-1"), null));
        Assert.Null(PushAlertProjector.BuildTopic(Constants.PortalBaseUrl, Encoding.UTF8.GetBytes("key")));
    }

    [Fact]
    public void System_messages_never_share_a_tag_so_none_replaces_an_earlier_notification()
    {
        var scope = NotificationScope.Tenant("11111111-1111-1111-1111-111111111111");
        var now = new DateTime(2026, 10, 8, 18, 38, 40, DateTimeKind.Utc);
        var first = PushAlertProjector.SystemMessage("session_watch", "Enrollment finished", "A finished.", "success", scope, now, Constants.PortalSessionUrl("sess-1"));
        var second = PushAlertProjector.SystemMessage("session_watch", "Enrollment finished", "B finished.", "success", scope, now, Constants.PortalSessionUrl("sess-2"));

        Assert.Equal(first.Id, first.Tag);
        Assert.Equal(second.Id, second.Tag);
        Assert.NotEqual(first.Tag, second.Tag);
        using var json = JsonDocument.Parse(PushAlertProjector.ToJsonBytes(first));
        Assert.Equal(first.Id, json.RootElement.GetProperty("notification").GetProperty("tag").GetString());
    }
}
