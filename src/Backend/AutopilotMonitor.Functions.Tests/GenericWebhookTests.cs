using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the generic webhook provider: payload rendering, custom-header parsing/denylist,
/// and the save-time header validation.
/// </summary>
public class GenericWebhookTests
{
    // ── GenericJsonRenderer ───────────────────────────────────────────────

    [Fact]
    public void GenericJsonRenderer_RendersStableSchema()
    {
        var alert = new NotificationAlert
        {
            EventType = "enrollment_succeeded",
            Title = "✅ Enrollment Succeeded",
            Summary = "Enrollment Succeeded: DESK-001",
            Severity = NotificationSeverity.Success,
            ThemeColor = "00B050",
            Facts = { new NotificationFact { Name = "Device", Value = "DESK-001" } },
            Actions = { new NotificationAction { Type = "openUrl", Title = "Open session", Url = "https://portal.example.com/sessions/abc" } },
        };

        var json = new GenericJsonRenderer().RenderToJson(alert);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("enrollment_succeeded", root.GetProperty("eventType").GetString());
        Assert.Equal("Success", root.GetProperty("severity").GetString());
        Assert.Equal("Enrollment Succeeded: DESK-001", root.GetProperty("summary").GetString());
        Assert.Equal("https://portal.example.com/sessions/abc", root.GetProperty("primaryUrl").GetString());

        var facts = root.GetProperty("facts");
        Assert.Equal(1, facts.GetArrayLength());
        Assert.Equal("Device", facts[0].GetProperty("name").GetString());
        Assert.Equal("DESK-001", facts[0].GetProperty("value").GetString());
    }

    [Fact]
    public void GenericJsonRenderer_OmitsEventTypeAndSessionUrl_WhenAbsent()
    {
        var alert = new NotificationAlert
        {
            Title = "Test",
            Summary = "Test",
            Severity = NotificationSeverity.Info,
        };

        var json = new GenericJsonRenderer().RenderToJson(alert);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("eventType", out _));
        Assert.False(root.TryGetProperty("primaryUrl", out _));
        Assert.Equal(WebhookProviderType.GenericJson, new GenericJsonRenderer().ProviderType);
    }

    [Theory]
    [InlineData(true, "enrollment_succeeded")]
    [InlineData(false, "enrollment_failed")]
    public void BuildEnrollmentAlert_SetsEventType(bool success, string expected)
    {
        var alert = NotificationAlertBuilder.BuildEnrollmentAlert(
            "DESK", "SN", "Contoso", "Model", success, failureReason: "x", duration: null);

        Assert.Equal(expected, alert.EventType);
    }

    [Fact]
    public void BuildHardwareRejectedAlert_SetsEventType()
    {
        var alert = NotificationAlertBuilder.BuildHardwareRejectedAlert("Contoso", "Model", "SN");
        Assert.Equal("hardware_rejected", alert.EventType);
    }

    // ── NotificationChannel.GetCustomHeaders ──────────────────────────────

    [Fact]
    public void GetCustomHeaders_ReturnsEmpty_ForNonGenericProvider()
    {
        var channel = new NotificationChannel
        {
            ProviderType = (int)WebhookProviderType.Slack,
            CustomHeadersJson = "{\"Authorization\":\"Bearer x\"}",
        };

        Assert.Empty(channel.GetCustomHeaders());
    }

    [Fact]
    public void GetCustomHeaders_ParsesHeaders_ForGenericProvider()
    {
        var channel = new NotificationChannel
        {
            ProviderType = (int)WebhookProviderType.GenericJson,
            CustomHeadersJson = "{\"Authorization\":\"Bearer abc\",\"X-Api-Key\":\"k1\"}",
        };

        var headers = channel.GetCustomHeaders();
        Assert.Equal("Bearer abc", headers["Authorization"]);
        Assert.Equal("k1", headers["X-Api-Key"]);
    }

    [Fact]
    public void GetCustomHeaders_DropsRestrictedHeaders()
    {
        var channel = new NotificationChannel
        {
            ProviderType = (int)WebhookProviderType.GenericJson,
            CustomHeadersJson = "{\"Host\":\"evil.example.com\",\"Content-Length\":\"0\",\"X-Ok\":\"v\"}",
        };

        var headers = channel.GetCustomHeaders();
        Assert.False(headers.ContainsKey("Host"));
        Assert.False(headers.ContainsKey("Content-Length"));
        Assert.True(headers.ContainsKey("X-Ok"));
    }

    [Fact]
    public void GetCustomHeaders_ReturnsEmpty_ForMalformedJson()
    {
        var channel = new NotificationChannel
        {
            ProviderType = (int)WebhookProviderType.GenericJson,
            CustomHeadersJson = "{not valid",
        };

        Assert.Empty(channel.GetCustomHeaders());
    }

    // ── TenantConfigValidation.ValidateWebhookCustomHeaders ────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"Authorization\":\"Bearer abc\"}")]
    public void ValidateWebhookCustomHeaders_Accepts_EmptyOrValid(string? json)
    {
        Assert.Null(TenantConfigValidation.ValidateWebhookCustomHeaders(json));
    }

    [Theory]
    [InlineData("{not json", "not valid JSON.")]
    [InlineData("[\"a\",\"b\"]", "must be a JSON object")]
    [InlineData("{\"X-Key\":123}", "must have a string value")]
    [InlineData("{\"Bad Header\":\"v\"}", "not a valid HTTP header name")]
    [InlineData("{\"X-Key\":\"line1\\nline2\"}", "must not contain line breaks")]
    public void ValidateWebhookCustomHeaders_Rejects_Invalid(string json, string expectedFragment)
    {
        var error = TenantConfigValidation.ValidateWebhookCustomHeaders(json);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error);
    }
}
