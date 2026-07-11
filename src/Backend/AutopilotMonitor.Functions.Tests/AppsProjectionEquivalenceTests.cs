using System;
using System.Collections.Generic;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins that the column-projected AppInstallSummaries scans driving the app endpoints produce
/// identical aggregation inputs to the full-row drain — <c>AppMetricsProjection</c> for
/// metrics/app (+global) and <c>AppsDashboardProjection</c> for the App Dashboard
/// (list / analytics / sessions). As in the sibling equivalence suites, the "projected" row is
/// built with the projected keys ONLY, exactly like a live <c>$select</c> response.
/// </summary>
public class AppsProjectionEquivalenceTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000a99";

    private static readonly TableStorageService Sut =
        new(new Mock<TableServiceClient>().Object, NullLogger<TableStorageService>.Instance);

    /// <summary>Full wide AppInstallSummaries row with every column family populated.</summary>
    private static TableEntity FullRow()
    {
        return new TableEntity(TenantId, Guid.NewGuid().ToString())
        {
            ["SessionId"] = "sess-1",
            ["TenantId"] = TenantId,
            ["AppName"] = "Contoso App",
            ["AppType"] = "Win32",
            ["AppVersion"] = "2.1.0",
            ["Status"] = "Failed",
            ["StartedAt"] = new DateTimeOffset(DateTime.UtcNow.AddHours(-6)),
            ["CompletedAt"] = new DateTimeOffset(DateTime.UtcNow.AddHours(-5)),
            ["DurationSeconds"] = 340,
            ["DownloadBytes"] = 180_000_000L,
            ["DownloadDurationSeconds"] = 25,
            ["AttemptNumber"] = 2,
            ["InstallerPhase"] = "Install",
            ["FailureCode"] = "0x80070643",
            ["FailureMessage"] = "Fatal error during installation. " + new string('z', 400),
            ["ExitCode"] = 1603,
            ["DetectionResult"] = "NotDetected",
            // DO telemetry block (dashboard-irrelevant, app-metrics-relevant).
            ["DoDownloadMode"] = 1,
            ["DoFileSize"] = 180_000_000L,
            ["DoTotalBytesDownloaded"] = 180_000_000L,
            ["DoBytesFromPeers"] = 40_000_000L,
            ["DoBytesFromHttp"] = 140_000_000L,
            ["DoBytesFromLanPeers"] = 40_000_000L,
            ["DoBytesFromGroupPeers"] = 0L,
            ["DoBytesFromInternetPeers"] = 0L,
            ["DoBytesFromLinkLocalPeers"] = 0L,
            ["DoBytesFromCacheServer"] = 2_000_000L,
            ["DoPercentPeerCaching"] = 22,
            ["DoDownloadDuration"] = "00:00:25",
            ["DoCacheHost"] = "mcc.contoso.local",
        };
    }

    private static TableEntity Project(TableEntity full, string[] projection)
    {
        var keep = new HashSet<string>(projection, StringComparer.Ordinal);
        var projected = new TableEntity(full.PartitionKey, full.RowKey);
        foreach (var kv in full)
        {
            if (keep.Contains(kv.Key))
                projected[kv.Key] = kv.Value;
        }
        return projected;
    }

    [Fact]
    public void AppMetrics_projection_preserves_ranking_and_DO_rollup_inputs()
    {
        var full = FullRow();
        var fromFull = Sut.MapToAppInstallSummary(full);
        var fromProjected = Sut.MapToAppInstallSummary(Project(full, TableStorageService.AppMetricsProjection));

        Assert.Equal(fromFull.AppName, fromProjected.AppName);
        Assert.Equal(fromFull.Status, fromProjected.Status);
        Assert.Equal(fromFull.StartedAt, fromProjected.StartedAt);
        Assert.Equal(fromFull.DurationSeconds, fromProjected.DurationSeconds);
        Assert.Equal(fromFull.DownloadBytes, fromProjected.DownloadBytes);
        Assert.Equal(fromFull.FailureCode, fromProjected.FailureCode);

        var aggFull = DoAggregator.Compute(new List<AppInstallSummary> { fromFull });
        var aggProjected = DoAggregator.Compute(new List<AppInstallSummary> { fromProjected });
        Assert.Equal(aggFull.DoAppCount, aggProjected.DoAppCount);
        Assert.Equal(aggFull.BytesFromPeers, aggProjected.BytesFromPeers);
        Assert.Equal(aggFull.BytesFromHttp, aggProjected.BytesFromHttp);
        Assert.Equal(aggFull.TotalBytesDownloaded, aggProjected.TotalBytesDownloaded);
        Assert.Equal(aggFull.BytesFromCacheServer, aggProjected.BytesFromCacheServer);
        Assert.Equal(aggFull.PercentPeerCaching, aggProjected.PercentPeerCaching);
    }

    [Fact]
    public void AppsDashboard_projection_preserves_list_analytics_and_sessions_inputs()
    {
        var full = FullRow();
        var fromFull = Sut.MapToAppInstallSummary(full);
        var fromProjected = Sut.MapToAppInstallSummary(Project(full, TableStorageService.AppsDashboardProjection));

        // TenantId doubles as the session-join key in the global aggregated view.
        Assert.Equal(fromFull.TenantId, fromProjected.TenantId);
        Assert.Equal(fromFull.SessionId, fromProjected.SessionId);
        Assert.Equal(fromFull.AppName, fromProjected.AppName);
        Assert.Equal(fromFull.AppType, fromProjected.AppType);
        Assert.Equal(fromFull.AppVersion, fromProjected.AppVersion);
        Assert.Equal(fromFull.Status, fromProjected.Status);
        Assert.Equal(fromFull.StartedAt, fromProjected.StartedAt);
        Assert.Equal(fromFull.CompletedAt, fromProjected.CompletedAt);
        Assert.Equal(fromFull.DurationSeconds, fromProjected.DurationSeconds);
        Assert.Equal(fromFull.DownloadBytes, fromProjected.DownloadBytes);
        Assert.Equal(fromFull.AttemptNumber, fromProjected.AttemptNumber);
        Assert.Equal(fromFull.InstallerPhase, fromProjected.InstallerPhase);
        Assert.Equal(fromFull.FailureCode, fromProjected.FailureCode);
        Assert.Equal(fromFull.FailureMessage, fromProjected.FailureMessage);
        Assert.Equal(fromFull.ExitCode, fromProjected.ExitCode);
        Assert.Equal(fromFull.DetectionResult, fromProjected.DetectionResult);
    }

    [Fact]
    public void AppsDashboard_projection_falls_back_to_PartitionKey_for_TenantId()
    {
        // Legacy rows without a TenantId column resolve TenantId from the PartitionKey — the
        // projection must not break that fallback (PartitionKey is always transferred).
        var full = FullRow();
        full.Remove("TenantId");
        var fromProjected = Sut.MapToAppInstallSummary(Project(full, TableStorageService.AppsDashboardProjection));
        Assert.Equal(TenantId, fromProjected.TenantId);
    }
}
