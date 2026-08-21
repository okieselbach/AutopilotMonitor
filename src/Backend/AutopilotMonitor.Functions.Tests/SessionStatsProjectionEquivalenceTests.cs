using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins that the column-projected SessionsIndex scan driving <c>GetSessionStatsAsync</c> produces
/// byte-identical dashboard stats to the old full-row drain — in particular that the omitted
/// columns can never shift the (historically fragile) DurationSeconds calculation.
///
/// The stats drain reads only <c>SessionStatsProjection</c>
/// { PartitionKey, RowKey, SessionId, Status, StartedAt, CompletedAt, DurationSeconds }. In real
/// Azure Table Storage a <c>$select</c> returns ONLY those properties; every other column is
/// genuinely absent from the entity. These tests reproduce that faithfully: the "projected" row is
/// built with the projected keys ONLY (no IsPreProvisioned / ResumedAt / noise), so a getter for an
/// omitted column returns null exactly as it would against the live service. We then map both shapes
/// through the production mapper (<see cref="TableStorageService.MapIndexEntityToSessionSummary"/>)
/// and through <see cref="TableStorageService.AggregateSessionStats"/> and assert equivalence.
/// </summary>
public class SessionStatsProjectionEquivalenceTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000abc";

    private static readonly TableStorageService Sut =
        new(new Mock<TableServiceClient>().Object, NullLogger<TableStorageService>.Instance);

    // SessionsIndex RowKey = inverted-tick prefix + "_" + sessionId (see ComputeIndexRowKey).
    private static string IndexRowKey(DateTime startedAt, string sessionId)
        => $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";

    /// <summary>Full ~40-column mirror row as the list drain would read it (no projection).</summary>
    private static TableEntity FullRow(
        string sessionId, string status, DateTime startedAt,
        int? durationSeconds, DateTime? completedAt,
        bool isPreProvisioned, DateTime? resumedAt)
    {
        var e = new TableEntity(TenantId, IndexRowKey(startedAt, sessionId))
        {
            ["SessionId"] = sessionId,
            ["Status"] = status,
            ["StartedAt"] = new DateTimeOffset(startedAt),
            ["IsPreProvisioned"] = isPreProvisioned,
            // Representative noise that the stats never read — present on a real mirror row, absent
            // on the projected one. Proves the projection drops them without changing the outcome.
            ["SerialNumber"] = "SN-FULL",
            ["DeviceName"] = "PC-FULL",
            ["Manufacturer"] = "Contoso",
            ["Model"] = "Model-X",
            ["OsName"] = "Windows 11",
            ["GeoCountry"] = "DE",
            ["EventCount"] = 123,
            ["PlatformScriptCount"] = 4,
            ["FailureSnapshotJson"] = "{\"big\":\"" + new string('x', 2000) + "\"}",
        };
        if (durationSeconds.HasValue) e["DurationSeconds"] = durationSeconds.Value;
        if (completedAt.HasValue) e["CompletedAt"] = new DateTimeOffset(completedAt.Value);
        if (resumedAt.HasValue) e["ResumedAt"] = new DateTimeOffset(resumedAt.Value);
        return e;
    }

    /// <summary>
    /// Reduces a full mirror row to exactly what Azure returns under the production
    /// <c>$select = SessionStatsProjection</c>: the projected data columns plus the structural
    /// PartitionKey/RowKey; every other property is genuinely absent. Deriving the keep-set from
    /// the production array (rather than hardcoding it) means dropping a column there — e.g.
    /// CompletedAt — would immediately fail the fallback test below.
    /// </summary>
    private static TableEntity Project(TableEntity full)
    {
        var keep = new HashSet<string>(TableStorageService.SessionStatsProjection, StringComparer.Ordinal);
        var projected = new TableEntity(full.PartitionKey, full.RowKey);
        foreach (var kv in full)
        {
            // PartitionKey/RowKey/Timestamp/ETag are structural (set via ctor / system-managed).
            if (kv.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag") continue;
            if (keep.Contains(kv.Key)) projected[kv.Key] = kv.Value;
        }
        return projected;
    }

    // ── DurationSeconds equivalence — the historically fragile field ─────────────

    [Fact]
    public void Succeeded_whiteglove_with_stored_duration_maps_identically_despite_dropped_columns()
    {
        // The decisive case: a WhiteGlove (IsPreProvisioned + ResumedAt) Succeeded session. The full
        // row carries both columns; the projected row drops them. They feed ONLY the Part-2 branch,
        // which is gated on status==InProgress, so a Succeeded session must resolve to the stored
        // duration on BOTH shapes. If the projection were unsafe this is where it would break.
        var started = DateTime.UtcNow.AddHours(-3);
        var sid = Guid.NewGuid().ToString();

        var fullRow = FullRow(
            sid, "Succeeded", started, durationSeconds: 1800, completedAt: started.AddSeconds(1800),
            isPreProvisioned: true, resumedAt: started.AddMinutes(20));
        var full = Sut.MapIndexEntityToSessionSummary(fullRow);
        var projected = Sut.MapIndexEntityToSessionSummary(Project(fullRow));

        Assert.Equal(1800, full.DurationSeconds);
        Assert.Equal(full.DurationSeconds, projected.DurationSeconds);
        Assert.Equal(full.Status, projected.Status);
        Assert.Equal(full.StartedAt, projected.StartedAt);
        Assert.Equal(sid, projected.SessionId);
    }

    [Fact]
    public void Succeeded_without_stored_duration_falls_back_to_completedAt_on_both_shapes()
    {
        // No stored DurationSeconds → ComputeEffectiveDuration uses (CompletedAt - StartedAt).
        // CompletedAt IS in the projection precisely so this fallback stays identical; this test
        // would fail if CompletedAt were ever dropped from SessionStatsProjection.
        var started = DateTime.UtcNow.AddHours(-2);
        var completed = started.AddSeconds(1200);
        var sid = Guid.NewGuid().ToString();

        var fullRow = FullRow(
            sid, "Succeeded", started, durationSeconds: null, completedAt: completed,
            isPreProvisioned: false, resumedAt: null);
        var full = Sut.MapIndexEntityToSessionSummary(fullRow);
        var projected = Sut.MapIndexEntityToSessionSummary(Project(fullRow));

        Assert.Equal(1200, full.DurationSeconds);
        Assert.Equal(full.DurationSeconds, projected.DurationSeconds);
    }

    // ── End-to-end: projected drain yields identical SessionStats ────────────────

    [Fact]
    public void AggregateSessionStats_is_identical_for_full_vs_projected_fleet()
    {
        var now = DateTime.UtcNow;
        // A representative mixed fleet covering every status the cards count, including a WhiteGlove
        // Succeeded (drops IsPreProvisioned/ResumedAt), a CompletedAt-fallback Succeeded, and an
        // InProgress WhiteGlove whose per-row duration DOES diverge between shapes but which the
        // tally never reads — so the aggregate must still match exactly.
        var fleet = new (string Status, DateTime Started, int? Dur, DateTime? Completed, bool Wg, DateTime? Resumed)[]
        {
            ("Succeeded",  now.AddHours(-1),  1800, now.AddHours(-1).AddSeconds(1800), false, null),
            ("Succeeded",  now.AddHours(-2),  600,  now.AddHours(-2).AddSeconds(600),  true,  now.AddHours(-2).AddMinutes(15)), // WhiteGlove
            ("Succeeded",  now.AddHours(-3),  null, now.AddHours(-3).AddSeconds(900),  false, null), // CompletedAt fallback
            ("Failed",     now.AddHours(-4),  7200, now.AddHours(-4).AddSeconds(7200), false, null), // duration ignored for Failed
            ("InProgress", now.AddMinutes(-30), 60, null,                              true,  now.AddMinutes(-10)), // WG in-flight: per-row dur diverges, ignored
            ("Pending",    now.AddDays(-2),   null, null,                              true,  null),
            ("Stalled",    now.AddHours(-9),  null, null,                              false, null),
            ("Succeeded",  now.Date.AddHours(2), 1200, now.Date.AddHours(2).AddSeconds(1200), false, null), // today
            ("Failed",     now.Date.AddHours(3), null, null,                          false, null), // today
        };

        var fullSummaries = new List<SessionSummary>();
        var projectedSummaries = new List<SessionSummary>();
        foreach (var (status, started, dur, completed, wg, resumed) in fleet)
        {
            var sid = Guid.NewGuid().ToString();
            var fullRow = FullRow(sid, status, started, dur, completed, wg, resumed);
            fullSummaries.Add(Sut.MapIndexEntityToSessionSummary(fullRow));
            projectedSummaries.Add(Sut.MapIndexEntityToSessionSummary(Project(fullRow)));
        }

        var full = TableStorageService.AggregateSessionStats(fullSummaries, days: 30);
        var projected = TableStorageService.AggregateSessionStats(projectedSummaries, days: 30);

        Assert.Equal(full.Days, projected.Days);
        Assert.Equal(full.ActiveCount, projected.ActiveCount);
        Assert.Equal(full.TotalLastNDays, projected.TotalLastNDays);
        Assert.Equal(full.SucceededLastNDays, projected.SucceededLastNDays);
        Assert.Equal(full.FailedLastNDays, projected.FailedLastNDays);
        Assert.Equal(full.SuccessRatePct, projected.SuccessRatePct);
        Assert.Equal(full.AvgDurationMinutes, projected.AvgDurationMinutes); // duration-sensitive
        Assert.Equal(full.MedianDurationMinutes, projected.MedianDurationMinutes); // the headline card
        Assert.Equal(full.P90DurationMinutes, projected.P90DurationMinutes);
        Assert.Equal(full.TotalToday, projected.TotalToday);
        Assert.Equal(full.FailedToday, projected.FailedToday);
    }
}
