using System;
using System.Collections.Generic;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins that the column-projected scans driving the geographic endpoints produce identical
/// aggregation inputs to the full-row drains — sessions via <c>GeoMetricsSessionProjection</c>
/// (Geo* grouping fields + effective-duration inputs) and app installs via
/// <c>GeoAppInstallProjection</c> (join key, window filter, throughput inputs and every counter
/// <see cref="DoAggregator"/> sums). As in the sibling equivalence suites, the "projected" row is
/// built with the projected keys ONLY, exactly like a live <c>$select</c> response.
/// </summary>
public class GeoMetricsProjectionEquivalenceTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000fe0";

    private static readonly TableStorageService Sut =
        new(new Mock<TableServiceClient>().Object, NullLogger<TableStorageService>.Instance);

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

    // ===== Sessions (map aggregation) =====

    private static TableEntity FullSessionRow(string sessionId, string status, DateTime startedAt, int? durationSeconds)
    {
        var e = new TableEntity(TenantId, sessionId)
        {
            ["Status"] = status,
            ["StartedAt"] = new DateTimeOffset(startedAt),
            ["GeoCountry"] = "DE",
            ["GeoRegion"] = "Hessen",
            ["GeoCity"] = "Frankfurt",
            ["GeoLoc"] = "50.11,8.68",
            ["IsPreProvisioned"] = false,
            // Noise the geo aggregation never reads.
            ["SerialNumber"] = "SN-FULL",
            ["Manufacturer"] = "Contoso",
            ["OsName"] = "Windows 11",
            ["FailureSnapshotJson"] = "{\"big\":\"" + new string('x', 2000) + "\"}",
        };
        if (durationSeconds.HasValue) e["DurationSeconds"] = durationSeconds.Value;
        return e;
    }

    [Fact]
    public void Session_projection_preserves_geo_grouping_and_duration_inputs()
    {
        var full = FullSessionRow("s-geo", "Succeeded", DateTime.UtcNow.AddHours(-4), durationSeconds: 2400);
        var fromFull = Sut.MapToSessionSummary(full);
        var fromProjected = Sut.MapToSessionSummary(Project(full, TableStorageService.GeoMetricsSessionProjection));

        Assert.Equal(fromFull.TenantId, fromProjected.TenantId);
        Assert.Equal(fromFull.SessionId, fromProjected.SessionId);
        Assert.Equal(fromFull.Status, fromProjected.Status);
        Assert.Equal(fromFull.StartedAt, fromProjected.StartedAt);
        Assert.Equal(fromFull.DurationSeconds, fromProjected.DurationSeconds);
        Assert.Equal(fromFull.GeoCountry, fromProjected.GeoCountry);
        Assert.Equal(fromFull.GeoRegion, fromProjected.GeoRegion);
        Assert.Equal(fromFull.GeoCity, fromProjected.GeoCity);
        Assert.Equal(fromFull.GeoLoc, fromProjected.GeoLoc);

        // The grouping key must be identical too — it is composed from the Geo* fields.
        Assert.Equal(
            GetGeographicMetricsFunction.GetLocationKey(fromFull, "city"),
            GetGeographicMetricsFunction.GetLocationKey(fromProjected, "city"));
    }

    // ===== App installs (throughput + Delivery Optimization) =====

    private static TableEntity FullAppRow(string sessionId)
    {
        return new TableEntity(TenantId, Guid.NewGuid().ToString())
        {
            ["SessionId"] = sessionId,
            ["AppName"] = "Contoso App",
            ["StartedAt"] = new DateTimeOffset(DateTime.UtcNow.AddHours(-3)),
            ["DownloadBytes"] = 250_000_000L,
            ["DownloadDurationSeconds"] = 42,
            ["DoDownloadMode"] = 1,
            ["DoTotalBytesDownloaded"] = 250_000_000L,
            ["DoBytesFromPeers"] = 100_000_000L,
            ["DoBytesFromHttp"] = 150_000_000L,
            ["DoBytesFromLanPeers"] = 60_000_000L,
            ["DoBytesFromGroupPeers"] = 30_000_000L,
            ["DoBytesFromInternetPeers"] = 10_000_000L,
            ["DoBytesFromLinkLocalPeers"] = 0L,
            ["DoBytesFromCacheServer"] = 5_000_000L,
            // Noise the geo aggregation never reads.
            ["Status"] = "Succeeded",
            ["FailureMessage"] = new string('y', 500),
            ["DetectionResult"] = "Detected",
        };
    }

    [Fact]
    public void App_projection_preserves_throughput_inputs_and_DoAggregator_result()
    {
        var full = FullAppRow("s-geo");
        var fromFull = Sut.MapToAppInstallSummary(full);
        var fromProjected = Sut.MapToAppInstallSummary(Project(full, TableStorageService.GeoAppInstallProjection));

        Assert.Equal(fromFull.SessionId, fromProjected.SessionId);
        Assert.Equal(fromFull.StartedAt, fromProjected.StartedAt);
        Assert.Equal(fromFull.DownloadBytes, fromProjected.DownloadBytes);
        Assert.Equal(fromFull.DownloadDurationSeconds, fromProjected.DownloadDurationSeconds);

        var aggFull = DoAggregator.Compute(new List<AppInstallSummary> { fromFull });
        var aggProjected = DoAggregator.Compute(new List<AppInstallSummary> { fromProjected });
        Assert.Equal(aggFull.DoAppCount, aggProjected.DoAppCount);
        Assert.Equal(aggFull.BytesFromPeers, aggProjected.BytesFromPeers);
        Assert.Equal(aggFull.BytesFromHttp, aggProjected.BytesFromHttp);
        Assert.Equal(aggFull.TotalBytesDownloaded, aggProjected.TotalBytesDownloaded);
        Assert.Equal(aggFull.BytesFromLanPeers, aggProjected.BytesFromLanPeers);
        Assert.Equal(aggFull.BytesFromGroupPeers, aggProjected.BytesFromGroupPeers);
        Assert.Equal(aggFull.BytesFromInternetPeers, aggProjected.BytesFromInternetPeers);
        Assert.Equal(aggFull.BytesFromLinkLocalPeers, aggProjected.BytesFromLinkLocalPeers);
        Assert.Equal(aggFull.BytesFromCacheServer, aggProjected.BytesFromCacheServer);
        Assert.Equal(aggFull.PercentPeerCaching, aggProjected.PercentPeerCaching);
    }

    [Fact]
    public void App_projection_preserves_no_DO_telemetry_marker()
    {
        // DoDownloadMode maps to -1 when the column is absent — a projected row without DO columns
        // must stay "no telemetry" (DoAggregator filters on DoDownloadMode >= 0), not become 0/mode.
        var full = FullAppRow("s-nodo");
        full.Remove("DoDownloadMode");
        var fromProjected = Sut.MapToAppInstallSummary(Project(full, TableStorageService.GeoAppInstallProjection));
        Assert.Equal(-1, fromProjected.DoDownloadMode);
        Assert.False(DoAggregator.Compute(new List<AppInstallSummary> { fromProjected }).HasTelemetry);
    }
}
