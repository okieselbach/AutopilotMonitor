using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the app-version duration regression radar (<see cref="AppVersionRegressionRadar"/>):
/// detection gates (≥10 measured installs on both sides, median lift ≥2×, ≥300s absolute),
/// first-seen version ordering (never string sort), the measured-population exclusions,
/// re-arm semantics, the tracker keyspace round-trip, the wording contract, and the
/// versionBreakdown duration fields on the analytics wire shape.
/// </summary>
public class AppVersionRegressionRadarTests
{
    private const string TenantA = "00000000-0000-0000-0000-0000000000a1";
    private const string App = "Contoso VPN";
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private static int _sessionCounter;

    private static AppInstallSummary Install(
        string version,
        int durationSeconds,
        DateTime? startedAt = null,
        string appName = App,
        string status = "Succeeded",
        string terminalState = "Installed",
        bool collision = false)
        => new()
        {
            TenantId = TenantA,
            SessionId = $"s-{++_sessionCounter}",
            AppName = appName,
            AppVersion = version,
            Status = status,
            TerminalState = terminalState,
            DurationSeconds = durationSeconds,
            StartedAt = startedAt ?? Now.AddDays(-1),
            AppIdCollision = collision,
        };

    /// <summary>n measured installs of one version, all with the same duration, spread over one day.</summary>
    private static List<AppInstallSummary> Batch(string version, int count, int durationSeconds, DateTime firstSeen)
        => Enumerable.Range(0, count)
            .Select(i => Install(version, durationSeconds, firstSeen.AddMinutes(i)))
            .ToList();

    // ── detection gates ─────────────────────────────────────────────────────

    [Theory]
    // currentCount, previousCount, previousMedian, currentMedian, expectFire
    [InlineData(10, 10, 300, 900, true)]    // lift 3×, +600s — fires
    [InlineData(10, 10, 300, 600, true)]    // exactly 2× AND exactly +300s — boundary fires
    [InlineData(9, 10, 300, 900, false)]    // current side below sample floor
    [InlineData(10, 9, 300, 900, false)]    // previous side below sample floor
    [InlineData(10, 10, 200, 499, false)]   // lift 2.5× but only +299s — absolute floor blocks
    [InlineData(10, 10, 1000, 1900, false)] // +900s but lift 1.9× — lift gate blocks
    public void Evaluate_Gates(int currentCount, int previousCount, int previousMedian, int currentMedian, bool expectFire)
    {
        var rows = Batch("1.0", previousCount, previousMedian, Now.AddDays(-20));
        rows.AddRange(Batch("2.0", currentCount, currentMedian, Now.AddDays(-5)));

        var findings = AppVersionRegressionRadar.Evaluate(rows);

        if (!expectFire)
        {
            Assert.Empty(findings);
            return;
        }

        var finding = Assert.Single(findings);
        Assert.Equal(App, finding.AppName);
        Assert.Equal("2.0", finding.CurrentVersion);
        Assert.Equal("1.0", finding.PreviousVersion);
        Assert.Equal(currentMedian, finding.CurrentMedianSeconds);
        Assert.Equal(previousMedian, finding.PreviousMedianSeconds);
        Assert.Equal(currentCount, finding.CurrentMeasuredCount);
        Assert.Equal(previousCount, finding.PreviousMeasuredCount);
        Assert.Equal(Math.Round((double)currentMedian / previousMedian, 1), finding.Lift);
    }

    [Fact]
    public void Evaluate_MedianIsNearestRank_HandComputed()
    {
        // Even n: nearest-rank p50 takes the LOWER middle value (rank = ceil(0.5*4)-1 = 1).
        var rows = new List<AppInstallSummary>();
        foreach (var d in new[] { 100, 200, 300, 400 }) // median 200
            rows.AddRange(Batch("1.0", 3, d, Now.AddDays(-20)));
        foreach (var d in new[] { 500, 600, 700, 800 }) // median 600
            rows.AddRange(Batch("2.0", 3, d, Now.AddDays(-5)));

        var finding = Assert.Single(AppVersionRegressionRadar.Evaluate(rows));
        Assert.Equal(600, finding.CurrentMedianSeconds);
        Assert.Equal(200, finding.PreviousMedianSeconds);
        Assert.Equal(3.0, finding.Lift);
    }

    // ── version ordering ────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_VersionOrder_ByFirstSeen_NeverStringSort()
    {
        // Ordinally "9.1" > "2024.10", but "2024.10" is the NEWER version by first-seen.
        // A string sort would flip current/previous and compare in the wrong direction.
        var rows = Batch("9.1", 10, 300, Now.AddDays(-20));
        rows.AddRange(Batch("2024.10", 10, 900, Now.AddDays(-5)));

        var finding = Assert.Single(AppVersionRegressionRadar.Evaluate(rows));
        Assert.Equal("2024.10", finding.CurrentVersion);
        Assert.Equal("9.1", finding.PreviousVersion);
    }

    [Fact]
    public void Evaluate_MultipleVersionsInFlight_PreviousIsLatestFirstSeenBeforeCurrent()
    {
        var rows = Batch("1.0", 10, 300, Now.AddDays(-30));  // oldest
        rows.AddRange(Batch("1.1", 10, 320, Now.AddDays(-18))); // the true predecessor
        rows.AddRange(Batch("2.0", 10, 960, Now.AddDays(-4)));  // newest, regressed

        var finding = Assert.Single(AppVersionRegressionRadar.Evaluate(rows));
        Assert.Equal("2.0", finding.CurrentVersion);
        Assert.Equal("1.1", finding.PreviousVersion); // not 1.0
        Assert.Equal(320, finding.PreviousMedianSeconds);
    }

    [Fact]
    public void Evaluate_SingleVersion_NeverFires()
    {
        var rows = Batch("1.0", 25, 900, Now.AddDays(-5));
        Assert.Empty(AppVersionRegressionRadar.Evaluate(rows));
    }

    // ── measured-population exclusions ──────────────────────────────────────

    [Fact]
    public void Evaluate_ExcludedRows_DoNotCountTowardSamplesOrMedians()
    {
        var rows = Batch("1.0", 10, 300, Now.AddDays(-20));
        rows.AddRange(Batch("2.0", 10, 900, Now.AddDays(-5)));

        // None of these may move the samples or medians:
        rows.Add(Install("2.0", 0, Now.AddDays(-2)));                                  // unmeasured (start unobserved)
        rows.Add(Install("2.0", 21601, Now.AddDays(-2)));                              // over the 6h plausibility cap
        rows.Add(Install("2.0", 50, Now.AddDays(-2), terminalState: "Skipped"));       // skip — not an attempt
        rows.Add(Install("2.0", 50, Now.AddDays(-2), terminalState: "Postponed"));     // skip — not an attempt
        rows.Add(Install("2.0", 50, Now.AddDays(-2), status: "Failed"));               // failed — no duration signal
        rows.Add(Install("2.0", 50, Now.AddDays(-2), collision: true));                // AppId collision — foreign outcomes
        rows.Add(Install("", 50, Now.AddDays(-2)));                                    // no version
        rows.Add(Install("2.0", 50, Now.AddDays(-2), appName: ""));                    // no app name

        var finding = Assert.Single(AppVersionRegressionRadar.Evaluate(rows));
        Assert.Equal(10, finding.CurrentMeasuredCount);
        Assert.Equal(900, finding.CurrentMedianSeconds);
    }

    [Fact]
    public void Evaluate_DeterministicOrdering_LiftDesc_ThenAppNameOrdinal()
    {
        var rows = new List<AppInstallSummary>();
        rows.AddRange(Batch("1.0", 10, 300, Now.AddDays(-20)).Select(s => { s.AppName = "App B"; return s; }));
        rows.AddRange(Batch("2.0", 10, 900, Now.AddDays(-5)).Select(s => { s.AppName = "App B"; return s; }));  // lift 3.0
        rows.AddRange(Batch("1.0", 10, 300, Now.AddDays(-20)).Select(s => { s.AppName = "App A"; return s; }));
        rows.AddRange(Batch("2.0", 10, 1500, Now.AddDays(-5)).Select(s => { s.AppName = "App A"; return s; })); // lift 5.0

        var findings = AppVersionRegressionRadar.Evaluate(rows);
        Assert.Equal(2, findings.Count);
        Assert.Equal("App A", findings[0].AppName); // higher lift first
        Assert.Equal("App B", findings[1].AppName);
    }

    // ── re-arm ──────────────────────────────────────────────────────────────

    private static AppVersionRegressionAlert Alert(
        string currentVersion = "2.0", string previousVersion = "1.0")
        => new()
        {
            TenantId = TenantA,
            AppName = App,
            CurrentVersion = currentVersion,
            PreviousVersion = previousVersion,
            CurrentMedianSeconds = 900,
            PreviousMedianSeconds = 300,
            CurrentMeasuredCount = 10,
            PreviousMeasuredCount = 10,
            Lift = 3.0,
            FirstNotifiedAt = Now.AddDays(-3),
            LastEvaluatedAt = Now.AddDays(-1),
        };

    [Fact]
    public void ShouldReArm_WhenCurrentVersionDrainedOutOfHorizon()
    {
        // Only the previous version still has measured installs — the alerted version is gone.
        var rows = Batch("1.0", 10, 300, Now.AddDays(-20));
        Assert.True(AppVersionRegressionRadar.ShouldReArm(rows, Alert()));
    }

    [Fact]
    public void ShouldReArm_WhenCurrentVersionBelowSampleFloor()
    {
        var rows = Batch("1.0", 10, 300, Now.AddDays(-20));
        rows.AddRange(Batch("2.0", 9, 900, Now.AddDays(-5)));
        Assert.True(AppVersionRegressionRadar.ShouldReArm(rows, Alert()));
    }

    [Fact]
    public void ShouldReArm_WhenMedianFellBackUnderReArmFactor()
    {
        // 400 < 1.5 × 300 — the regression recovered (e.g. CDN warmed up).
        var rows = Batch("1.0", 10, 300, Now.AddDays(-20));
        rows.AddRange(Batch("2.0", 10, 400, Now.AddDays(-5)));
        Assert.True(AppVersionRegressionRadar.ShouldReArm(rows, Alert()));
    }

    [Fact]
    public void ShouldNotReArm_WhileMedianStaysElevated()
    {
        // 500 ≥ 1.5 × 300 — still elevated (even though below the 2× fire gate).
        var rows = Batch("1.0", 10, 300, Now.AddDays(-20));
        rows.AddRange(Batch("2.0", 10, 500, Now.AddDays(-5)));
        Assert.False(AppVersionRegressionRadar.ShouldReArm(rows, Alert()));
    }

    [Fact]
    public void ShouldNotReArm_WhenPreviousVersionDrained_NoComparisonBasis()
    {
        // The predecessor left the horizon; the episode is kept until the current
        // version itself drains (fires-stopped-style re-arm only).
        var rows = Batch("2.0", 10, 900, Now.AddDays(-5));
        Assert.False(AppVersionRegressionRadar.ShouldReArm(rows, Alert()));
    }

    // ── tracker keyspace (round-trip + key shape) ───────────────────────────

    [Fact]
    public void AppVersionRegressionRowKey_PrefixedCaseFoldedAndSanitized()
    {
        Assert.Equal("appversionregression|contoso app_2|1.0_beta_",
            TableHardwareRejectionNotificationTracker.BuildAppVersionRegressionRowKey(
                "  Contoso App/2 ", " 1.0#Beta? "));
    }

    [Fact]
    public void AppVersionRegressionRowKey_NoPrefixCollisionWithOtherKeyspaces()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildAppVersionRegressionRowKey(App, "1.0");
        Assert.StartsWith("appversionregression|", key, StringComparison.Ordinal);
        // The prefix range [prefix, "appversionregression}") must not swallow the sibling
        // keyspaces ("ruleregression|", "tpmpss|", "{mfr}|{model}").
        Assert.True(string.CompareOrdinal("ruleregression|", "appversionregression}") > 0);
        Assert.True(string.CompareOrdinal("tpmpss|", "appversionregression}") > 0);
    }

    [Fact]
    public void AppVersionRegressionEntity_RoundTripsAllFields()
    {
        var alert = new AppVersionRegressionAlert
        {
            TenantId = TenantA,
            AppName = App,
            CurrentVersion = "2.4.0",
            PreviousVersion = "2.3.9",
            CurrentMedianSeconds = 1740,
            PreviousMedianSeconds = 660,
            CurrentMeasuredCount = 12,
            PreviousMeasuredCount = 40,
            Lift = 2.6,
            FirstNotifiedAt = Now,
            LastEvaluatedAt = Now.AddHours(2),
        };

        var entity = TableHardwareRejectionNotificationTracker.BuildAppVersionRegressionEntity(TenantA, alert);
        Assert.Equal(TenantA.ToLowerInvariant(), entity.PartitionKey);
        Assert.Equal("appversionregression|contoso vpn|2.4.0", entity.RowKey);

        var mapped = TableHardwareRejectionNotificationTracker.MapToAppVersionRegressionAlert(entity);
        Assert.Equal(TenantA, mapped.TenantId);
        Assert.Equal(App, mapped.AppName);
        Assert.Equal("2.4.0", mapped.CurrentVersion);
        Assert.Equal("2.3.9", mapped.PreviousVersion);
        Assert.Equal(1740, mapped.CurrentMedianSeconds);
        Assert.Equal(660, mapped.PreviousMedianSeconds);
        Assert.Equal(12, mapped.CurrentMeasuredCount);
        Assert.Equal(40, mapped.PreviousMeasuredCount);
        Assert.Equal(2.6, mapped.Lift);
        Assert.Equal(Now, mapped.FirstNotifiedAt);
        Assert.Equal(Now.AddHours(2), mapped.LastEvaluatedAt);
    }

    // ── wording contract ────────────────────────────────────────────────────

    [Fact]
    public void RegressionMessage_CarriesTheFullNumbers_MinutesOneDecimal()
    {
        var finding = new AppVersionDurationRegressionFinding
        {
            TenantId = TenantA,
            AppName = App,
            CurrentVersion = "2.4.0",
            PreviousVersion = "2.3.9",
            CurrentMedianSeconds = 1740,
            PreviousMedianSeconds = 660,
            CurrentMeasuredCount = 12,
            PreviousMeasuredCount = 40,
            Lift = 2.6,
        };

        Assert.Equal(
            "Median install duration rose from 11 to 29 min after version 2.4.0 " +
            "(12 measured installs vs 40 on version 2.3.9) — lift 2.6x over the last 35 days.",
            MaintenanceService.BuildAppVersionRegressionMessage(finding));
    }

    // ── analytics wire shape (versionBreakdown duration fields) ─────────────

    [Fact]
    public async Task Analytics_VersionBreakdown_CarriesMeasuredDurationStats()
    {
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SessionSummary?)null);

        var rows = new List<AppInstallSummary>
        {
            Install("1.0", 100), Install("1.0", 200), Install("1.0", 300), Install("1.0", 400),
            Install("1.0", 0),                                  // unmeasured — excluded from stats
            Install("1.0", 50, terminalState: "Skipped"),       // skip — excluded
            Install("1.0", 50, status: "Failed"),               // failed — excluded from durations
        };

        var root = JsonSerializer.SerializeToElement(await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            rows, sessionRepo.Object, App, days: 30));

        var version = root.GetProperty("versionBreakdown")[0];
        Assert.Equal("1.0", version.GetProperty("appVersion").GetString());
        Assert.Equal(7, version.GetProperty("installs").GetInt32());
        Assert.Equal(4, version.GetProperty("measuredInstalls").GetInt32());
        // Nearest-rank over [100, 200, 300, 400]: p50 = 200 (lower middle), p95 = 400.
        Assert.Equal(200, version.GetProperty("medianDurationSeconds").GetInt32());
        Assert.Equal(400, version.GetProperty("p95DurationSeconds").GetInt32());
    }

    [Fact]
    public async Task Analytics_VersionRegressions_SerializedCamelCase_AndPresentWhenEmptyData()
    {
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SessionSummary?)null);

        var alert = Alert();
        // The Functions host serializes camelCase on the wire; mirror that here so the
        // asserted property names match what the web client actually parses.
        var wireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Empty-data early return still surfaces the block (episodes can outlive window data).
        var emptyRoot = JsonSerializer.SerializeToElement(await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            new List<AppInstallSummary>(), sessionRepo.Object, App, days: 30,
            new List<AppVersionRegressionAlert> { alert }), wireOptions);
        Assert.Equal(1, emptyRoot.GetProperty("versionRegressions").GetArrayLength());

        var root = JsonSerializer.SerializeToElement(await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
            new List<AppInstallSummary> { Install("1.0", 100) }, sessionRepo.Object, App, days: 30,
            new List<AppVersionRegressionAlert> { alert }), wireOptions);

        var regression = root.GetProperty("versionRegressions")[0];
        Assert.Equal("2.0", regression.GetProperty("currentVersion").GetString());
        Assert.Equal(900, regression.GetProperty("currentMedianSeconds").GetInt32());
    }
}
