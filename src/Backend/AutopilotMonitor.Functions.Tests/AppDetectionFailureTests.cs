using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// "IME did not detect the app after its installer finished" is IME's error 0x87D1041C, never the
/// <c>detectionResult</c> field (IME's check before the install, D-392). The IME log writes the
/// code as the signed decimal -2016345060, the ESP path as hex: the stored failure code is
/// canonical so rankings and the apps banner see one code.
/// </summary>
public class AppDetectionFailureTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000fa2";

    [Theory]
    [InlineData("0x87d1041c", true)]
    [InlineData("0x87D1041C", true)]
    [InlineData("-2016345060", true)]
    [InlineData("esp_apps_detection_failure", true)]
    [InlineData("ESP_APPS_DETECTION_FAILURE", true)]
    [InlineData("0x87d1041d", false)]       // the uninstall twin: still found after the removal
    [InlineData("-2016345059", false)]
    [InlineData("1603", false)]
    [InlineData("esp_apps_timeout", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDetectionFailure_recognises_every_form_of_0x87D1041C(string? failureCode, bool expected)
        => Assert.Equal(expected, Constants.AppFailureTypes.IsDetectionFailure(failureCode));

    private static AppInstallSummary Failed(string? failureCode)
    {
        var evt = new EnrollmentEvent
        {
            EventType = "app_install_failed",
            Timestamp = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc),
            Data = new Dictionary<string, object> { ["appName"] = "Contoso App", ["detectionResult"] = "NotDetected" },
        };
        if (failureCode != null) evt.Data["errorCode"] = failureCode;
        var summaries = new Dictionary<string, AppInstallAggregationState>(StringComparer.OrdinalIgnoreCase);
        EventIngestProcessor.AggregateAppInstallEvent(evt, TenantId, "session", summaries);
        return summaries["Contoso App"].Summary;
    }

    [Theory]
    [InlineData("-2016345060", "0x87d1041c")]
    [InlineData("0x87D1041C", "0x87d1041c")]
    [InlineData("-2145091577", "0x80248007")]
    [InlineData("1603", "1603")]                // an exit code stays an exit code
    [InlineData(" 0x80070643 ", "0x80070643")]
    public void Ingest_stores_the_failure_code_canonical(string sent, string stored)
        => Assert.Equal(stored, Failed(sent).FailureCode);

    [Fact]
    public void Ingest_keeps_the_failure_type_ahead_of_the_code()
    {
        var evt = new EnrollmentEvent
        {
            EventType = "app_install_failed",
            Timestamp = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc),
            Data = new Dictionary<string, object>
            {
                ["appName"] = "Contoso App",
                ["errorCode"] = "0x87d1041c",
                ["failureType"] = Constants.AppFailureTypes.EspAppsDetectionFailure,
            },
        };
        var summaries = new Dictionary<string, AppInstallAggregationState>(StringComparer.OrdinalIgnoreCase);
        EventIngestProcessor.AggregateAppInstallEvent(evt, TenantId, "session", summaries);

        Assert.Equal(Constants.AppFailureTypes.EspAppsDetectionFailure, summaries["Contoso App"].Summary.FailureCode);
    }

    private static AppInstallSummary Row(string status, string failureCode = "", string sessionId = "s-1") => new()
    {
        TenantId = TenantId,
        SessionId = sessionId,
        AppName = "Contoso App",
        Status = status,
        FailureCode = failureCode,
        StartedAt = DateTime.UtcNow.AddHours(-2),
        DurationSeconds = 30,
    };

    private static async Task<JsonElement> AnalyticsAsync(List<AppInstallSummary> summaries)
    {
        var sessions = new Mock<ISessionRepository>();
        sessions.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SessionSummary?)null);
        var response = await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            summaries, sessions.Object, "Contoso App", AppInstallSources.Ime, days: 30);
        return TestWire.SerializeToElement(response);
    }

    [Fact]
    public async Task Banner_counts_failed_installs_IME_did_not_detect_afterwards()
    {
        var root = await AnalyticsAsync(new List<AppInstallSummary>
        {
            Row("Failed", "0x87d1041c", "s-1"),
            Row("Failed", Constants.AppFailureTypes.EspAppsDetectionFailure, "s-2"),
            Row("Failed", "0x80248007", "s-3"),     // a download error
            Row("Failed", "", "s-4"),               // no code recorded
            Row("Succeeded", "", "s-5"),            // installed and detected
        });

        Assert.Equal(2, root.GetProperty("detectionFailureCount").GetInt32());
    }

    [Fact]
    public async Task Top_failure_codes_rank_both_IME_forms_as_one_code()
    {
        var root = await AnalyticsAsync(new List<AppInstallSummary>
        {
            Row("Failed", Failed("-2016345060").FailureCode, "s-1"),
            Row("Failed", Failed("0x87D1041C").FailureCode, "s-2"),
            Row("Failed", Failed("1603").FailureCode, "s-3"),
        });

        var top = root.GetProperty("topFailureCodes")[0];
        Assert.Equal("0x87d1041c", top.GetProperty("code").GetString());
        Assert.Equal(2, top.GetProperty("count").GetInt32());
    }
}
