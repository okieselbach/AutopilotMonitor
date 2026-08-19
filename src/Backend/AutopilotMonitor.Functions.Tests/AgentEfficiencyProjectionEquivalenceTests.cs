using System;
using System.Collections.Generic;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins that the column-projected SessionsIndex scan driving
/// <c>AgentEfficiencyMetricsService</c> maps every field the aggregation reads
/// (SessionId, TenantId, DeviceName, AgentVersion, AvgApiLatencyMs, ApiRequestCount,
/// StartedAt) identically to the full-row drain. Same fixture discipline as
/// <see cref="SessionStatsProjectionEquivalenceTests"/>: the projected row carries the
/// projection's keys ONLY, so a getter for an omitted column returns null exactly as it
/// would against live storage — and dropping a load-bearing column from the production
/// array fails here immediately.
/// </summary>
public class AgentEfficiencyProjectionEquivalenceTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000abc";

    private static readonly TableStorageService Sut =
        new(new Mock<TableServiceClient>().Object, NullLogger<TableStorageService>.Instance);

    private static string IndexRowKey(DateTime startedAt, string sessionId)
        => $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";

    private static TableEntity FullRow(string sessionId, DateTime startedAt)
        => new TableEntity(TenantId, IndexRowKey(startedAt, sessionId))
        {
            ["SessionId"] = sessionId,
            ["Status"] = "Succeeded",
            ["StartedAt"] = new DateTimeOffset(startedAt),
            ["AgentVersion"] = "2.0.1400",
            ["DeviceName"] = "PC-EFF",
            ["AvgApiLatencyMs"] = 84.5,
            ["ApiRequestCount"] = 321,
            // Representative noise the efficiency scan never reads.
            ["SerialNumber"] = "SN-FULL",
            ["Manufacturer"] = "Contoso",
            ["Model"] = "Model-X",
            ["OsName"] = "Windows 11",
            ["GeoCountry"] = "DE",
            ["EventCount"] = 123,
            ["FailureSnapshotJson"] = "{\"big\":\"" + new string('x', 2000) + "\"}",
        };

    private static TableEntity Project(TableEntity full)
    {
        var keep = new HashSet<string>(AgentEfficiencyMetricsService.SessionIndexProjection, StringComparer.Ordinal);
        var projected = new TableEntity(full.PartitionKey, full.RowKey);
        foreach (var kv in full)
        {
            if (kv.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag") continue;
            if (keep.Contains(kv.Key)) projected[kv.Key] = kv.Value;
        }
        return projected;
    }

    [Fact]
    public void Projected_row_maps_every_efficiency_relevant_field_identically()
    {
        var started = DateTime.UtcNow.AddHours(-2);
        var sid = Guid.NewGuid().ToString();
        var fullRow = FullRow(sid, started);

        var full = Sut.MapIndexEntityToSessionSummary(fullRow);
        var projected = Sut.MapIndexEntityToSessionSummary(Project(fullRow));

        Assert.Equal(sid, projected.SessionId);
        Assert.Equal(full.TenantId, projected.TenantId);
        Assert.Equal(full.DeviceName, projected.DeviceName);
        Assert.Equal(full.AgentVersion, projected.AgentVersion);
        Assert.Equal(full.AvgApiLatencyMs, projected.AvgApiLatencyMs);
        Assert.Equal(full.ApiRequestCount, projected.ApiRequestCount);
        // StartedAt feeds the repo's cross-tenant merge-sort and next-page cursor.
        Assert.Equal(full.StartedAt, projected.StartedAt);
    }

    [Fact]
    public void Projection_contains_the_repo_cursor_columns()
    {
        // FetchAllSessionsPageInternalAsync merge-sorts on StartedAt and rebuilds the cursor
        // from StartedAt + SessionId; a projection without them silently breaks pagination.
        Assert.Contains("StartedAt", AgentEfficiencyMetricsService.SessionIndexProjection);
        Assert.Contains("SessionId", AgentEfficiencyMetricsService.SessionIndexProjection);
        Assert.Contains("PartitionKey", AgentEfficiencyMetricsService.SessionIndexProjection);
        Assert.Contains("RowKey", AgentEfficiencyMetricsService.SessionIndexProjection);
    }
}
