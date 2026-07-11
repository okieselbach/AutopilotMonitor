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
/// Pins that the column-projected Sessions scan driving <c>GetUsageWindowSessionsAsync</c> produces
/// identical usage-metrics inputs to the old full-row drain. The usage compute reads only
/// <c>UsageMetricsSessionProjection</c>; in real Azure Table Storage a <c>$select</c> returns ONLY
/// those properties, so the "projected" row here is built with the projected keys only — a getter
/// for an omitted column returns null exactly as it would against the live service. Both shapes go
/// through the production mapper (<see cref="TableStorageService.MapToSessionSummary(TableEntity)"/>)
/// and every field UsageMetricsService consumes must match. Mirrors
/// <see cref="SessionStatsProjectionEquivalenceTests"/> for the stats drain.
/// </summary>
public class UsageMetricsProjectionEquivalenceTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000def";

    private static readonly TableStorageService Sut =
        new(new Mock<TableServiceClient>().Object, NullLogger<TableStorageService>.Instance);

    /// <summary>Full wide Sessions row as GetSessionsByDateRangeAsync would read it (no projection).</summary>
    private static TableEntity FullRow(
        string sessionId, string status, DateTime startedAt,
        int? durationSeconds, DateTime? completedAt,
        bool isPreProvisioned, DateTime? resumedAt)
    {
        var e = new TableEntity(TenantId, sessionId)
        {
            ["Status"] = status,
            ["StartedAt"] = new DateTimeOffset(startedAt),
            ["Manufacturer"] = "Contoso",
            ["Model"] = "Model-X",
            ["IsUserDriven"] = true,
            ["IsPreProvisioned"] = isPreProvisioned,
            ["PlatformScriptCount"] = 4,
            ["RemediationScriptCount"] = 2,
            // Representative noise the usage compute never reads — present on a real wide row,
            // absent on the projected one. Proves the projection drops it without changing results.
            ["SerialNumber"] = "SN-FULL",
            ["DeviceName"] = "PC-FULL",
            ["OsName"] = "Windows 11",
            ["GeoCountry"] = "DE",
            ["EventCount"] = 123,
            ["FailureSnapshotJson"] = "{\"big\":\"" + new string('x', 2000) + "\"}",
        };
        if (durationSeconds.HasValue) e["DurationSeconds"] = durationSeconds.Value;
        if (completedAt.HasValue) e["CompletedAt"] = new DateTimeOffset(completedAt.Value);
        if (resumedAt.HasValue) e["ResumedAt"] = new DateTimeOffset(resumedAt.Value);
        return e;
    }

    /// <summary>
    /// Reduces a full row to exactly what Azure returns under the production
    /// <c>$select = UsageMetricsSessionProjection</c>. Deriving the keep-set from the production
    /// array means dropping a column there — e.g. ResumedAt — immediately fails the WhiteGlove
    /// duration test below.
    /// </summary>
    private static TableEntity Project(TableEntity full)
    {
        var keep = new HashSet<string>(TableStorageService.UsageMetricsSessionProjection, StringComparer.Ordinal);
        var projected = new TableEntity(full.PartitionKey, full.RowKey);
        foreach (var kv in full)
        {
            if (keep.Contains(kv.Key))
                projected[kv.Key] = kv.Value;
        }
        return projected;
    }

    private static void AssertUsageFieldsEqual(SessionSummary fromFull, SessionSummary fromProjected)
    {
        Assert.Equal(fromFull.TenantId, fromProjected.TenantId);
        Assert.Equal(fromFull.SessionId, fromProjected.SessionId);
        Assert.Equal(fromFull.StartedAt, fromProjected.StartedAt);
        Assert.Equal(fromFull.CompletedAt, fromProjected.CompletedAt);
        Assert.Equal(fromFull.Status, fromProjected.Status);
        Assert.Equal(fromFull.Manufacturer, fromProjected.Manufacturer);
        Assert.Equal(fromFull.Model, fromProjected.Model);
        Assert.Equal(fromFull.IsUserDriven, fromProjected.IsUserDriven);
        Assert.Equal(fromFull.IsPreProvisioned, fromProjected.IsPreProvisioned);
        Assert.Equal(fromFull.PlatformScriptCount, fromProjected.PlatformScriptCount);
        Assert.Equal(fromFull.RemediationScriptCount, fromProjected.RemediationScriptCount);
        // Wall-clock duration branches call UtcNow inside the mapper; two map calls microseconds
        // apart may straddle a whole-second boundary, so allow 1s of slack instead of exactness.
        Assert.NotNull(fromFull.DurationSeconds);
        Assert.NotNull(fromProjected.DurationSeconds);
        Assert.True(Math.Abs(fromFull.DurationSeconds!.Value - fromProjected.DurationSeconds!.Value) <= 1,
            $"DurationSeconds diverged: full={fromFull.DurationSeconds} projected={fromProjected.DurationSeconds}");
    }

    [Fact]
    public void Succeeded_with_stored_duration_maps_identically()
    {
        var full = FullRow("s-ok", "Succeeded", DateTime.UtcNow.AddHours(-2),
            durationSeconds: 1800, completedAt: DateTime.UtcNow.AddHours(-1.5),
            isPreProvisioned: false, resumedAt: null);

        AssertUsageFieldsEqual(Sut.MapToSessionSummary(full), Sut.MapToSessionSummary(Project(full)));
    }

    [Fact]
    public void Failed_without_stored_duration_uses_completedAt_fallback_identically()
    {
        var full = FullRow("s-fail", "Failed", DateTime.UtcNow.AddHours(-3),
            durationSeconds: null, completedAt: DateTime.UtcNow.AddHours(-2),
            isPreProvisioned: false, resumedAt: null);

        AssertUsageFieldsEqual(Sut.MapToSessionSummary(full), Sut.MapToSessionSummary(Project(full)));
    }

    [Fact]
    public void InProgress_whiteglove_part2_duration_branch_survives_projection()
    {
        // This branch reads IsPreProvisioned + ResumedAt + DurationSeconds — exactly the columns
        // that would silently break if dropped from the projection.
        var full = FullRow("s-wg", "InProgress", DateTime.UtcNow.AddDays(-2),
            durationSeconds: 3600, completedAt: null,
            isPreProvisioned: true, resumedAt: DateTime.UtcNow.AddMinutes(-30));

        AssertUsageFieldsEqual(Sut.MapToSessionSummary(full), Sut.MapToSessionSummary(Project(full)));
    }

    [Fact]
    public void InProgress_wallclock_fallback_survives_projection()
    {
        var full = FullRow("s-run", "InProgress", DateTime.UtcNow.AddMinutes(-45),
            durationSeconds: null, completedAt: null,
            isPreProvisioned: false, resumedAt: null);

        AssertUsageFieldsEqual(Sut.MapToSessionSummary(full), Sut.MapToSessionSummary(Project(full)));
    }
}
