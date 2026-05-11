using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="NotificationAlertBuilder.BuildEnrollmentStartedAlert"/>, the
/// opt-in "enrollment started" webhook alert fired from <c>RegisterSessionFunction</c>.
/// </summary>
public class NotificationAlertBuilderStartTests
{
    [Fact]
    public void BuildEnrollmentStartedAlert_FreshSession_RendersStartTitle()
    {
        var alert = NotificationAlertBuilder.BuildEnrollmentStartedAlert(
            deviceName: "DESK-001",
            serialNumber: "SN-12345",
            manufacturer: "Contoso",
            model: "Latitude 7430",
            isResume: false,
            sessionUrl: "https://portal.example.com/session/t/s");

        Assert.Contains("Enrollment Started", alert.Title);
        Assert.DoesNotContain("Resumed", alert.Title);
        Assert.Equal(NotificationSeverity.Info, alert.Severity);
        Assert.Contains(alert.Facts, f => f.Name == "Device" && f.Value == "DESK-001");
        Assert.Contains(alert.Facts, f => f.Name == "Serial" && f.Value == "SN-12345");
        Assert.Contains(alert.Facts, f => f.Name == "Hardware" && f.Value == "Contoso Latitude 7430");
        Assert.Contains(alert.Actions, a => a.Type == "openUrl" && a.Url == "https://portal.example.com/session/t/s");
    }

    [Fact]
    public void BuildEnrollmentStartedAlert_WhiteGloveResume_RendersResumeTitle()
    {
        var alert = NotificationAlertBuilder.BuildEnrollmentStartedAlert(
            deviceName: "DESK-002",
            serialNumber: "SN-67890",
            manufacturer: "Fabrikam",
            model: "ProBook 450",
            isResume: true);

        Assert.Contains("Pre-Provisioning Resumed", alert.Title);
        Assert.DoesNotContain("Enrollment Started", alert.Title);
        Assert.Equal(NotificationSeverity.Info, alert.Severity);
        Assert.Empty(alert.Actions); // no sessionUrl provided
    }

    [Fact]
    public void BuildEnrollmentStartedAlert_NullDevice_FallsBackToDash()
    {
        var alert = NotificationAlertBuilder.BuildEnrollmentStartedAlert(
            deviceName: null, serialNumber: null, manufacturer: null, model: null,
            isResume: false);

        Assert.Contains("Unknown Device", alert.Summary);
        Assert.Contains(alert.Facts, f => f.Name == "Device" && f.Value == "–");
        Assert.Contains(alert.Facts, f => f.Name == "Hardware" && f.Value == "–");
    }
}
