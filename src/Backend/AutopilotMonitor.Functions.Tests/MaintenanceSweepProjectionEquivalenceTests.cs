using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins that the ONE projected window scan the maintenance tick shares across its rolling
/// sweeps (<c>GetMaintenanceWindowSessionsAsync</c> / <c>MaintenanceSweepSessionProjection</c>)
/// carries every field the time-attribution, device-journey and verdict-calibration sweeps
/// read. In Azure Table Storage a <c>$select</c> returns ONLY the listed properties, so the
/// projected row here is built from the production array — dropping a column there fails the
/// sweep that consumes it. Mirrors <see cref="UsageMetricsProjectionEquivalenceTests"/>.
/// </summary>
public class MaintenanceSweepProjectionEquivalenceTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000abc";
    private static readonly DateTime Started = new(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

    private static readonly TableStorageService Sut =
        new(new Mock<TableServiceClient>().Object, NullLogger<TableStorageService>.Instance);

    /// <summary>Full wide Sessions row with every column the sweeps read set to a distinct non-default value, plus noise.</summary>
    private static TableEntity FullRow() => new(TenantId, "11111111-1111-1111-1111-111111111111")
    {
        ["Status"] = "Succeeded",
        ["StartedAt"] = new DateTimeOffset(Started),
        ["CompletedAt"] = new DateTimeOffset(Started.AddHours(1)),
        ["LastEventAt"] = new DateTimeOffset(Started.AddHours(2)),
        ["DurationSeconds"] = 3600,
        ["EventCount"] = 321,
        ["DeletionState"] = "None",
        ["SerialNumber"] = "SN-1234",
        ["Manufacturer"] = "Contoso",
        ["Model"] = "Model-X",
        ["EnrollmentType"] = "v2",
        ["IsPreProvisioned"] = true,
        ["IsSelfDeployingProfile"] = true,
        ["IsUserDriven"] = false,
        ["ResumedAt"] = new DateTimeOffset(Started.AddMinutes(30)),
        ["AdminMarkedAction"] = "Succeeded",
        ["VerdictPath"] = "manual:succeeded",
        ["PriorStatus"] = "Incomplete",
        ["PriorVerdictPath"] = "sweep:r6",
        ["FailureReason"] = "No Device Setup completion or explicit failure signal observed before timeout",
        ["ReconcileReason"] = "Reconciled at timeout: user completed setup (desktop + Windows Hello)",
        ["FailureSource"] = "manual",
        ["EspSoftFailure"] = true,
        // Noise the sweeps never read — present on the wide row, dropped by the projection.
        ["DeviceName"] = "PC-FULL",
        ["OsName"] = "Windows 11",
        ["OsBuild"] = "26200.1234",
        ["GeoCountry"] = "DE",
        ["AgentVersion"] = "2.9.0",
        ["DiagnosticsBlobName"] = "diag.zip",
        ["FailureSnapshotJson"] = "{\"big\":\"" + new string('x', 2000) + "\"}",
    };

    private static TableEntity Project(TableEntity full)
    {
        var keep = new HashSet<string>(TableStorageService.MaintenanceSweepSessionProjection, StringComparer.Ordinal);
        var projected = new TableEntity(full.PartitionKey, full.RowKey);
        foreach (var kv in full)
        {
            if (keep.Contains(kv.Key)) projected[kv.Key] = kv.Value;
        }
        return projected;
    }

    [Fact]
    public void Projection_drops_the_wide_columns()
    {
        var projected = Project(FullRow());
        Assert.False(projected.ContainsKey("FailureSnapshotJson"));
        Assert.False(projected.ContainsKey("GeoCountry"));
        Assert.False(projected.ContainsKey("OsBuild"));
        Assert.True(projected.Count < FullRow().Count);
    }

    [Fact]
    public void Every_field_the_sweeps_read_survives_the_projection()
    {
        var full = Sut.MapToSessionSummary(FullRow());
        var proj = Sut.MapToSessionSummary(Project(FullRow()));

        // Shared / structural
        Assert.Equal(full.TenantId, proj.TenantId);
        Assert.Equal(full.SessionId, proj.SessionId);
        Assert.Equal(full.Status, proj.Status);
        Assert.Equal(full.StartedAt, proj.StartedAt);
        Assert.Equal(full.CompletedAt, proj.CompletedAt);
        Assert.Equal(full.DeletionState, proj.DeletionState);

        // Time attribution: Status/DurationSeconds/DeletionState/EventCount + enrollment class
        Assert.Equal(full.DurationSeconds, proj.DurationSeconds);
        Assert.Equal(full.EventCount, proj.EventCount);
        Assert.Equal(TimeAttributionCalculator.GetEnrollmentClass(full), TimeAttributionCalculator.GetEnrollmentClass(proj));

        // Device journeys: BuildSessionRef + BuildDeviceHistoryRow display fields
        Assert.Equal(full.SerialNumber, proj.SerialNumber);
        Assert.Equal(full.Manufacturer, proj.Manufacturer);
        Assert.Equal(full.Model, proj.Model);
        Assert.Equal(full.EnrollmentType, proj.EnrollmentType);
        Assert.Equal(full.IsPreProvisioned, proj.IsPreProvisioned);
        Assert.Equal(full.AdminMarkedAction, proj.AdminMarkedAction);
        var refFull = DeviceJourneyCalculator.BuildSessionRef(full)!;
        var refProj = DeviceJourneyCalculator.BuildSessionRef(proj)!;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(refFull), System.Text.Json.JsonSerializer.Serialize(refProj));

        // Verdict calibration: attribution + legacy derivation inputs + proxy timestamps
        Assert.Equal(full.LastEventAt, proj.LastEventAt);
        Assert.Equal(full.VerdictPath, proj.VerdictPath);
        Assert.Equal(full.PriorStatus, proj.PriorStatus);
        Assert.Equal(full.PriorVerdictPath, proj.PriorVerdictPath);
        Assert.Equal(full.FailureReason, proj.FailureReason);
        Assert.Equal(full.ReconcileReason, proj.ReconcileReason);
        Assert.Equal(full.FailureSource, proj.FailureSource);
        Assert.Equal(full.EspSoftFailure, proj.EspSoftFailure);
        Assert.Equal(VerdictPathDerivation.Derive(full), VerdictPathDerivation.Derive(proj));
        // Unstamped variant exercises the derivation on the projected inputs.
        var unstampedFull = FullRow(); unstampedFull.Remove("VerdictPath"); unstampedFull.Remove("AdminMarkedAction"); unstampedFull.Remove("FailureSource");
        Assert.Equal(VerdictPathDerivation.Derive(Sut.MapToSessionSummary(unstampedFull)),
                     VerdictPathDerivation.Derive(Sut.MapToSessionSummary(Project(unstampedFull))));
    }

    [Fact]
    public void Aggregation_cores_produce_identical_rows_from_the_projected_scan()
    {
        var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var fullList = new List<SessionSummary> { Sut.MapToSessionSummary(FullRow()) };
        var projList = new List<SessionSummary> { Sut.MapToSessionSummary(Project(FullRow())) };
        var histories = new Dictionary<string, List<DeviceHistory>>();

        var a = MaintenanceService.BuildVerdictCalibrationAggregates(fullList, histories, now);
        var b = MaintenanceService.BuildVerdictCalibrationAggregates(projList, histories, now);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
    }

    [Fact]
    public void SliceSweepWindow_keeps_sessions_from_the_window_start()
    {
        var window = new List<SessionSummary>
        {
            new() { SessionId = "old", StartedAt = Started.AddDays(-40) },
            new() { SessionId = "edge", StartedAt = Started },
            new() { SessionId = "new", StartedAt = Started.AddDays(1) },
        };
        var sliced = MaintenanceService.SliceSweepWindow(window, Started);
        Assert.Equal(new[] { "edge", "new" }, sliced.Select(s => s.SessionId));
    }

    [Fact]
    public void Shared_window_covers_every_sweeps_horizon()
    {
        // 7d window + 28d baseline anchored on yesterday = 35 days back ≥ the 30-day rolling sweeps.
        Assert.Equal(35, MaintenanceService.SweepWindowDays);
        Assert.True(MaintenanceService.SweepWindowDays >= 30);
    }
}
