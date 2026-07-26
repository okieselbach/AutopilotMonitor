using System.Collections.Generic;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the Delivery Optimization rollup + slowest/failing aggregation in
/// <see cref="MetricsMath.BuildAppMetricsPayload"/>, the single source of truth shared by the
/// tenant (metrics/app) and global (global/metrics/app) functions. The payload is an anonymous
/// projection, so we round-trip it through System.Text.Json and assert on the wire shape the
/// MCP tool / web UI actually receive.
/// </summary>
public class MetricsMathAppPayloadTests
{
    private static JsonElement Build(IEnumerable<AppInstallSummary> summaries)
    {
        var payload = MetricsMath.BuildAppMetricsPayload(summaries);
        return JsonSerializer.SerializeToElement(payload);
    }

    [Fact]
    public void EmptyInput_YieldsZeroedPayload()
    {
        var root = Build(new List<AppInstallSummary>());

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("totalApps").GetInt32());
        Assert.Equal(0, root.GetProperty("totalInstalls").GetInt32());

        var doRollup = root.GetProperty("deliveryOptimization");
        Assert.Equal(0, doRollup.GetProperty("totalBytesDownloaded").GetInt64());
        Assert.Equal(0d, doRollup.GetProperty("peerOffloadPercent").GetDouble());
    }

    [Fact]
    public void DeliveryOptimizationRollup_SumsPeersAndCacheServerForOffload()
    {
        // 1000 bytes total: 400 peers + 100 MCC + 500 CDN → (400+100)/1000 = 50% offload.
        var summaries = new List<AppInstallSummary>
        {
            new()
            {
                AppName = "Contoso App", Status = "Succeeded", DurationSeconds = 30, DoDownloadMode = 0,
                DoTotalBytesDownloaded = 600, DoBytesFromPeers = 400, DoBytesFromCacheServer = 0, DoBytesFromHttp = 200,
            },
            new()
            {
                AppName = "Contoso App", Status = "Succeeded", DurationSeconds = 40, DoDownloadMode = 0,
                DoTotalBytesDownloaded = 400, DoBytesFromPeers = 0, DoBytesFromCacheServer = 100, DoBytesFromHttp = 300,
            },
        };

        var root = Build(summaries);
        var doRollup = root.GetProperty("deliveryOptimization");

        Assert.Equal(1000, doRollup.GetProperty("totalBytesDownloaded").GetInt64());
        Assert.Equal(400, doRollup.GetProperty("fromPeers").GetInt64());
        Assert.Equal(100, doRollup.GetProperty("fromCacheServer").GetInt64());
        Assert.Equal(500, doRollup.GetProperty("fromHttp").GetInt64());
        Assert.Equal(50d, doRollup.GetProperty("peerOffloadPercent").GetDouble());
    }

    [Fact]
    public void DeliveryOptimization_FallsBackToPeersPlusHttpWhenTotalMissing()
    {
        // Legacy telemetry: source bytes are reported but DoTotalBytesDownloaded is 0. The rollup
        // must fall back to peers + http for the denominator so the data is not silently dropped.
        var summaries = new List<AppInstallSummary>
        {
            new()
            {
                AppName = "Legacy App", Status = "Succeeded", DurationSeconds = 20, DoDownloadMode = 0,
                DoTotalBytesDownloaded = 0, DoBytesFromPeers = 300, DoBytesFromHttp = 100,
            },
        };

        var doRollup = Build(summaries).GetProperty("deliveryOptimization");

        Assert.Equal(400, doRollup.GetProperty("totalBytesDownloaded").GetInt64());
        Assert.Equal(300, doRollup.GetProperty("fromPeers").GetInt64());
        // peerOffloadPercent = 300 / 400 = 75%
        Assert.Equal(75d, doRollup.GetProperty("peerOffloadPercent").GetDouble());
    }

    [Fact]
    public void PerApp_CarriesDoFieldsAndFailureCodes()
    {
        var summaries = new List<AppInstallSummary>
        {
            new() { AppName = "App A", Status = "Failed", FailureCode = "0x80070005" },
            new() { AppName = "App A", Status = "Failed", FailureCode = "0x80070005" },
            new()
            {
                AppName = "App A", Status = "Succeeded", DurationSeconds = 10, DoDownloadMode = 0,
                DoTotalBytesDownloaded = 200, DoBytesFromPeers = 50,
            },
        };

        var root = Build(summaries);

        Assert.Equal(1, root.GetProperty("totalApps").GetInt32());
        Assert.Equal(3, root.GetProperty("totalInstalls").GetInt32());

        var topFailing = root.GetProperty("topFailingApps");
        Assert.Equal(1, topFailing.GetArrayLength());
        var app = topFailing[0];
        Assert.Equal("App A", app.GetProperty("appName").GetString());
        Assert.Equal(2, app.GetProperty("failed").GetInt32());
        Assert.Equal(200, app.GetProperty("doTotalBytesDownloaded").GetInt64());
        Assert.Equal(50, app.GetProperty("doBytesFromPeers").GetInt64());
        // peerOffloadPercent = 50/200 = 25%
        Assert.Equal(25d, app.GetProperty("peerOffloadPercent").GetDouble());

        var topCode = app.GetProperty("topFailureCodes")[0];
        Assert.Equal("0x80070005", topCode.GetProperty("code").GetString());
        Assert.Equal(2, topCode.GetProperty("count").GetInt32());
    }

    [Fact]
    public void FailureRate_IsOverFinishedInstalls_InProgressExcluded()
    {
        var summaries = new List<AppInstallSummary>
        {
            new() { AppName = "App A", Status = "Failed" },
            new() { AppName = "App A", Status = "Succeeded", DurationSeconds = 10 },
            new() { AppName = "App A", Status = "InProgress" },
            new() { AppName = "App A", Status = "InProgress" },
        };

        var app = Build(summaries).GetProperty("topFailingApps")[0];

        Assert.Equal(4, app.GetProperty("totalInstalls").GetInt32());
        // 1 / (1 + 1) = 50% (over finished), NOT 1 / 4 = 25% (over all installs).
        Assert.Equal(50d, app.GetProperty("failureRate").GetDouble());
    }

    // ── PR0 (2026-07-26): skip/unmeasured classification ────────────────────

    [Fact]
    public void Skips_LeaveTheFailureRate_AndAreReportedSeparately()
    {
        // 1 Failed + 1 real install + 3 skips ("Update for X" not applicable).
        // Rate must be 1/(1+1) = 50%, NOT 1/(1+4) = 20% — skips are not attempts.
        var summaries = new List<AppInstallSummary>
        {
            new() { AppName = "App A", Status = "Failed", TerminalState = "Error" },
            new() { AppName = "App A", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 30 },
            new() { AppName = "App A", Status = "Succeeded", TerminalState = "Skipped" },
            new() { AppName = "App A", Status = "Succeeded", TerminalState = "Skipped" },
            new() { AppName = "App A", Status = "Succeeded", TerminalState = "Postponed" },
        };

        var root = Build(summaries);
        var app = root.GetProperty("topFailingApps")[0];

        Assert.Equal(50d, app.GetProperty("failureRate").GetDouble());
        Assert.Equal(1, app.GetProperty("succeeded").GetInt32());
        Assert.Equal(3, app.GetProperty("skipped").GetInt32());
        Assert.Equal(0, app.GetProperty("unmeasured").GetInt32());
        Assert.Equal(3, root.GetProperty("totalSkipped").GetInt32());
    }

    [Fact]
    public void ZeroDurations_AreExcludedFromAverages_AndCountedAsUnmeasured()
    {
        // Two measured installs (100s, 200s) + one whose start was never observed (audit §0.5:
        // agent attach window — duration UNKNOWN). avg must be 150, not (100+200+0)/3 = 100.
        var summaries = new List<AppInstallSummary>
        {
            new() { AppName = "App A", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 100 },
            new() { AppName = "App A", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 200 },
            new() { AppName = "App A", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 0 },
        };

        var root = Build(summaries);
        Assert.Equal(1, root.GetProperty("totalUnmeasured").GetInt32());

        // Rebuild with a third measured row to clear the sample floor and read the average.
        summaries.Add(new AppInstallSummary { AppName = "App A", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 150 });
        var slowest = Build(summaries).GetProperty("slowestApps");
        Assert.Equal(1, slowest.GetArrayLength());
        Assert.Equal(150d, slowest[0].GetProperty("avgDurationSeconds").GetDouble());
        Assert.Equal(1, slowest[0].GetProperty("unmeasured").GetInt32());
    }

    [Fact]
    public void LegacyZeroDurationRows_StayInRate_ButNeverInDurations()
    {
        // Rows written before TerminalState existed: cannot be classified as skip vs unmeasured.
        // They keep the legacy rate behavior (counted as succeeded) and are excluded from
        // durations (counted as unmeasured — duration genuinely not measured).
        var summaries = new List<AppInstallSummary>
        {
            new() { AppName = "App A", Status = "Failed" },
            new() { AppName = "App A", Status = "Succeeded", DurationSeconds = 0 },   // legacy, no TerminalState
            new() { AppName = "App A", Status = "Succeeded", DurationSeconds = 60, TerminalState = "Installed" },
        };

        var app = Build(summaries).GetProperty("topFailingApps")[0];

        Assert.Equal(2, app.GetProperty("succeeded").GetInt32());      // legacy row stays in the rate
        Assert.Equal(33.3d, app.GetProperty("failureRate").GetDouble());
        Assert.Equal(1, app.GetProperty("unmeasured").GetInt32());
        Assert.Equal(0, app.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public void SlowestApps_GateOnMeasuredInstalls_NotOnSucceededCount()
    {
        // 3 succeeded but only 2 measured → below the floor; an app must not rank "fast"
        // (or at all) on a handful of unobserved zeros.
        var summaries = new List<AppInstallSummary>
        {
            new() { AppName = "Mostly Unmeasured", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 5 },
            new() { AppName = "Mostly Unmeasured", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 5 },
            new() { AppName = "Mostly Unmeasured", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 0 },
        };

        var slowest = Build(summaries).GetProperty("slowestApps");
        Assert.Equal(0, slowest.GetArrayLength());
    }

    [Fact]
    public void SlowestApps_DropsAppsBelowSampleFloor()
    {
        // App with only 1 success must be excluded from slowestApps (minSamples = 3),
        // even though it is the slowest, so a single unfinished install can't dominate.
        var summaries = new List<AppInstallSummary>();
        summaries.Add(new() { AppName = "Rare App", Status = "Succeeded", DurationSeconds = 9999 });
        for (var i = 0; i < 3; i++)
            summaries.Add(new() { AppName = "Common App", Status = "Succeeded", DurationSeconds = 10 });

        var root = Build(summaries);
        var slowest = root.GetProperty("slowestApps");

        Assert.Equal(1, slowest.GetArrayLength());
        Assert.Equal("Common App", slowest[0].GetProperty("appName").GetString());
    }
}
