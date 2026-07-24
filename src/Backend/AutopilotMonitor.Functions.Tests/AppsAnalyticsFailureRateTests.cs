using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the terminal-only failure-rate convention on the Apps dashboard aggregations
/// (<see cref="AppsAnalyticsHelper"/>): every failureRate is Failed / (Failed + Succeeded) —
/// finished installs only. "InProgress" rows (still installing, or orphaned by a session that
/// died mid-install) sit in the install counts but never dilute a rate, matching the enrollment
/// success-rate convention. Responses are anonymous projections, so tests assert on the
/// JSON wire shape like the sibling suites.
/// </summary>
public class AppsAnalyticsFailureRateTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000fa1";

    private static AppInstallSummary A(
        string status,
        string appName = "Contoso App",
        DateTime? startedAt = null,
        string sessionId = "s-1",
        string appVersion = "")
        => new()
        {
            TenantId = TenantId,
            SessionId = sessionId,
            AppName = appName,
            AppVersion = appVersion,
            Status = status,
            StartedAt = startedAt ?? DateTime.UtcNow.AddHours(-2),
            DurationSeconds = status == "Succeeded" ? 30 : 0,
        };

    // ── /apps/list ──────────────────────────────────────────────────────────

    [Fact]
    public void List_FailureRate_IsOverFinishedInstalls_InProgressExcluded()
    {
        var summaries = new List<AppInstallSummary>
        {
            A("Failed"),
            A("Succeeded"),
            A("InProgress"),
            A("InProgress"),
        };

        var root = JsonSerializer.SerializeToElement(
            AppsAnalyticsHelper.BuildAppsListResponse(summaries, days: 30));

        var app = root.GetProperty("apps")[0];
        Assert.Equal(4, app.GetProperty("totalInstalls").GetInt32());
        // 1 / (1 + 1) = 50% (over finished), NOT 1 / 4 = 25% (over all installs).
        Assert.Equal(50d, app.GetProperty("failureRate").GetDouble());
    }

    [Fact]
    public void List_FailureRate_IsZero_WhenNothingFinishedYet()
    {
        var summaries = new List<AppInstallSummary> { A("InProgress"), A("InProgress") };

        var root = JsonSerializer.SerializeToElement(
            AppsAnalyticsHelper.BuildAppsListResponse(summaries, days: 30));

        Assert.Equal(0d, root.GetProperty("apps")[0].GetProperty("failureRate").GetDouble());
    }

    [Fact]
    public void List_Trend_UsesFinishedRates_AndGatesOnFinishedSamples()
    {
        var now = DateTime.UtcNow;
        var firstHalf = now.AddDays(-20);  // days: 30 → midpoint at now - 15d
        var secondHalf = now.AddDays(-5);

        var summaries = new List<AppInstallSummary>();
        // First half: 1 failed / 5 finished = 20%.
        summaries.Add(A("Failed", startedAt: firstHalf));
        for (var i = 0; i < 4; i++) summaries.Add(A("Succeeded", startedAt: firstHalf));
        // Second half: 3 failed / 5 finished = 60% — plus 10 in-flight installs that
        // previously dragged the half's rate down to 20% and masked the regression.
        for (var i = 0; i < 3; i++) summaries.Add(A("Failed", startedAt: secondHalf));
        for (var i = 0; i < 2; i++) summaries.Add(A("Succeeded", startedAt: secondHalf));
        for (var i = 0; i < 10; i++) summaries.Add(A("InProgress", startedAt: secondHalf));

        var root = JsonSerializer.SerializeToElement(
            AppsAnalyticsHelper.BuildAppsListResponse(summaries, days: 30));

        var app = root.GetProperty("apps")[0];
        Assert.Equal("worsening", app.GetProperty("trend").GetString());
        Assert.Equal(40d, app.GetProperty("trendDelta").GetDouble());
    }

    [Fact]
    public void List_Trend_StaysStable_WhenAHalfLacksFiveFinishedInstalls()
    {
        var now = DateTime.UtcNow;
        var summaries = new List<AppInstallSummary>();
        // First half: 5 finished. Second half: 4 finished + many in-flight — the old
        // install-count gate would have passed; the finished-sample gate must not.
        for (var i = 0; i < 5; i++) summaries.Add(A("Succeeded", startedAt: now.AddDays(-20)));
        for (var i = 0; i < 4; i++) summaries.Add(A("Failed", startedAt: now.AddDays(-5)));
        for (var i = 0; i < 10; i++) summaries.Add(A("InProgress", startedAt: now.AddDays(-5)));

        var root = JsonSerializer.SerializeToElement(
            AppsAnalyticsHelper.BuildAppsListResponse(summaries, days: 30));

        var app = root.GetProperty("apps")[0];
        Assert.Equal("stable", app.GetProperty("trend").GetString());
        Assert.Equal(JsonValueKind.Null, app.GetProperty("trendDelta").ValueKind);
    }

    // ── /apps/{appName}/analytics ───────────────────────────────────────────

    private static async Task<JsonElement> BuildAnalyticsAsync(
        List<AppInstallSummary> summaries, ISessionRepository? sessionRepo = null)
    {
        if (sessionRepo == null)
        {
            var mock = new Mock<ISessionRepository>();
            mock.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((SessionSummary?)null);
            sessionRepo = mock.Object;
        }
        var result = await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            summaries, sessionRepo, "Contoso App", days: 30);
        return JsonSerializer.SerializeToElement(result);
    }

    [Fact]
    public async Task Analytics_SummaryVersionAndTimeSeriesRates_AreOverFinishedInstalls()
    {
        var summaries = new List<AppInstallSummary>
        {
            A("Failed", appVersion: "1.0"),
            A("Succeeded", appVersion: "1.0"),
            A("InProgress", appVersion: "1.0"),
            A("InProgress", appVersion: "1.0"),
        };

        var root = await BuildAnalyticsAsync(summaries);

        // Summary: 1 / (1 + 1) = 50%, NOT 1 / 4 = 25%.
        var summary = root.GetProperty("summary");
        Assert.Equal(4, summary.GetProperty("totalInstalls").GetInt32());
        Assert.Equal(50d, summary.GetProperty("failureRate").GetDouble());

        var version = root.GetProperty("versionBreakdown")[0];
        Assert.Equal(4, version.GetProperty("installs").GetInt32());
        Assert.Equal(50d, version.GetProperty("failureRate").GetDouble());

        // All four installs land in today's bucket → same terminal-only rate there.
        var buckets = root.GetProperty("timeSeries").EnumerateArray().ToList();
        var todayBucket = buckets.Single(b => b.GetProperty("installs").GetInt32() > 0);
        Assert.Equal(50d, todayBucket.GetProperty("failureRate").GetDouble());
    }

    [Fact]
    public async Task Analytics_DeviceModelBreakdown_RatesAndSampleFloor_UseFinishedInstalls()
    {
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(r => r.GetSessionAsync(TenantId, It.IsAny<string>()))
            .ReturnsAsync((string _, string sessionId) => new SessionSummary
            {
                TenantId = TenantId,
                SessionId = sessionId,
                Manufacturer = "Contoso",
                Model = sessionId.StartsWith("s-a") ? "ModelA" : "ModelB",
            });

        var summaries = new List<AppInstallSummary>();
        // ModelA: 5 finished (1 failed → 20%) + 3 in-flight → ranked, rate undiluted.
        summaries.Add(A("Failed", sessionId: "s-a-0"));
        for (var i = 1; i <= 4; i++) summaries.Add(A("Succeeded", sessionId: $"s-a-{i}"));
        for (var i = 5; i <= 7; i++) summaries.Add(A("InProgress", sessionId: $"s-a-{i}"));
        // ModelB: 4 finished + 6 in-flight — 10 installs, but below the 5-finished floor.
        for (var i = 0; i <= 3; i++) summaries.Add(A("Failed", sessionId: $"s-b-{i}"));
        for (var i = 4; i <= 9; i++) summaries.Add(A("InProgress", sessionId: $"s-b-{i}"));

        var root = await BuildAnalyticsAsync(summaries, sessionRepo.Object);

        var breakdown = root.GetProperty("deviceModelBreakdown");
        var model = Assert.Single(breakdown.EnumerateArray());
        Assert.Equal("ModelA", model.GetProperty("model").GetString());
        Assert.Equal(8, model.GetProperty("installs").GetInt32());
        Assert.Equal(20d, model.GetProperty("failureRate").GetDouble());
    }
}
