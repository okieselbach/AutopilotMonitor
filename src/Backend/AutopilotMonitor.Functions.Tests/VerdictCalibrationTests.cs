using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verdict calibration aggregate (docs/backend/verdict-calibration.md): the pure bucketing core,
/// the persistence round-trip and the matrix arithmetic of the read endpoint.
/// </summary>
public class VerdictCalibrationTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    private static SessionSummary Session(string tenant, string id, SessionStatus status, DateTime started,
        string? path = null, string? priorPath = null, string? priorStatus = null, string? adminMarked = null,
        DateTime? completed = null, string serial = "SN-1", string? failureReason = null, string? reconcileReason = null) => new()
    {
        TenantId = tenant, SessionId = id, SerialNumber = serial, Status = status, StartedAt = started,
        CompletedAt = completed, VerdictPath = path, PriorVerdictPath = priorPath, PriorStatus = priorStatus,
        AdminMarkedAction = adminMarked, FailureReason = failureReason ?? string.Empty,
        ReconcileReason = reconcileReason ?? string.Empty, FailureSource = string.Empty,
    };

    private static DeviceHistory History(string tenant, string serial, params (string Id, DateTime Started, DateTime? Completed, string Status)[] refs) => new()
    {
        TenantId = tenant, SerialKey = serial.ToLowerInvariant(), SerialNumber = serial,
        Chain = refs.Select(r => new DeviceSessionRef { SessionId = r.Id, StartedAt = r.Started, CompletedAt = r.Completed, Status = r.Status }).ToList(),
    };

    private static VerdictCalibrationBucket Bucket(VerdictCalibrationDailyAggregate row, string path, string status)
        => Assert.Single(row.Buckets, b => b.VerdictPath == path && b.Status == status);

    // ---- BuildVerdictCalibrationAggregates ----

    [Fact]
    public void Buckets_by_tenant_date_path_and_status_with_global_mirror()
    {
        var d1 = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var sessions = new List<SessionSummary>
        {
            Session(TenantA, "a1", SessionStatus.Succeeded, d1, path: "agent:complete", completed: d1.AddHours(1)),
            Session(TenantA, "a2", SessionStatus.Succeeded, d1.AddHours(2), path: "agent:complete", completed: d1.AddHours(3)),
            Session(TenantA, "a3", SessionStatus.Incomplete, d1, path: "sweep:r6", completed: d1.AddHours(6)),
            Session(TenantA, "a4", SessionStatus.Stalled, d1.AddDays(1), path: "sweep:stalled"),
            Session(TenantB, "b1", SessionStatus.Failed, d1, path: "agent:failed", completed: d1.AddHours(1)),
        };
        var rows = MaintenanceService.BuildVerdictCalibrationAggregates(sessions, new Dictionary<string, List<DeviceHistory>>(), Now);

        var a10 = Assert.Single(rows, r => r.TenantId == TenantA && r.Date == "2026-08-10");
        Assert.Equal(3, a10.SessionCount);
        Assert.Equal(3, a10.TerminalSessionCount);
        Assert.Equal(2, Bucket(a10, "agent:complete", "Succeeded").Count);
        Assert.Equal(1, Bucket(a10, "sweep:r6", "Incomplete").Count);

        var a11 = Assert.Single(rows, r => r.TenantId == TenantA && r.Date == "2026-08-11");
        Assert.Equal(1, a11.SessionCount);
        Assert.Equal(0, a11.TerminalSessionCount); // Stalled is not terminal
        Assert.Equal(1, Bucket(a11, "sweep:stalled", "Stalled").Count);

        var g10 = Assert.Single(rows, r => r.TenantId == "global" && r.Date == "2026-08-10");
        Assert.Equal(4, g10.SessionCount);
        Assert.Equal(1, Bucket(g10, "agent:failed", "Failed").Count);
        Assert.Equal(VerdictCalibrationDailyAggregate.CurrentVersion, g10.Version);
        Assert.Equal(Now, g10.ComputedAt);

        // Deterministic ordering: rows by tenant then date, buckets by path then status.
        Assert.Equal(rows.Select(r => (r.TenantId, r.Date)).OrderBy(k => k.TenantId, StringComparer.Ordinal).ThenBy(k => k.Date, StringComparer.Ordinal), rows.Select(r => (r.TenantId, r.Date)));
        Assert.Equal(a10.Buckets.Select(b => b.VerdictPath).OrderBy(p => p, StringComparer.Ordinal), a10.Buckets.Select(b => b.VerdictPath));
    }

    [Fact]
    public void Unstamped_rows_are_derived_and_counted_as_derived()
    {
        var d = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var sessions = new List<SessionSummary>
        {
            Session(TenantA, "a1", SessionStatus.Succeeded, d, path: null, completed: d.AddHours(1)), // legacy agent completion
            Session(TenantA, "a2", SessionStatus.Incomplete, d, path: null, completed: d.AddHours(5),
                failureReason: "No Device Setup completion or explicit failure signal observed before timeout"),
            Session(TenantA, "a3", SessionStatus.Succeeded, d, path: "agent:complete", completed: d.AddHours(1)),
        };
        var row = MaintenanceService.BuildVerdictCalibrationAggregates(sessions, new Dictionary<string, List<DeviceHistory>>(), Now)
            .Single(r => r.TenantId == TenantA);

        var complete = Bucket(row, "agent:complete", "Succeeded");
        Assert.Equal(2, complete.Count);
        Assert.Equal(1, complete.DerivedCount);
        var r6 = Bucket(row, "legacy:r6", "Incomplete");
        Assert.Equal(1, r6.Count);
        Assert.Equal(1, r6.DerivedCount);
    }

    [Fact]
    public void Overrides_are_attributed_to_the_prior_path()
    {
        var d = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var sessions = new List<SessionSummary>
        {
            // sweep said Incomplete, agent later completed → late completion against sweep:r5_incomplete
            Session(TenantA, "a1", SessionStatus.Succeeded, d, path: "agent:complete", priorPath: "sweep:r5_incomplete", priorStatus: "Incomplete", completed: d.AddHours(1)),
            // admin flipped an agent success to Failed
            Session(TenantA, "a2", SessionStatus.Failed, d, path: "manual:failed", priorPath: "agent:complete", priorStatus: "Succeeded", adminMarked: "Failed", completed: d.AddHours(1)),
            // retro reclassification Failed → Incomplete
            Session(TenantA, "a3", SessionStatus.Incomplete, d, path: "retro:r6", priorPath: "agent:failed", priorStatus: "Failed", completed: d.AddHours(1)),
            // admin mark to Succeeded counts as admin, not late completion
            Session(TenantA, "a4", SessionStatus.Succeeded, d, path: "manual:succeeded", priorPath: "sweep:r6", priorStatus: "Incomplete", adminMarked: "Succeeded", completed: d.AddHours(1)),
        };
        var row = MaintenanceService.BuildVerdictCalibrationAggregates(sessions, new Dictionary<string, List<DeviceHistory>>(), Now)
            .Single(r => r.TenantId == TenantA);

        var priorSweep = Bucket(row, "sweep:r5_incomplete", "Incomplete");
        Assert.Equal(0, priorSweep.Count); // nobody is on it any more
        Assert.Equal(1, priorSweep.OverriddenByLateCompletion);

        var priorAgent = Bucket(row, "agent:complete", "Succeeded");
        Assert.Equal(1, priorAgent.Count);  // a1 is on it now
        Assert.Equal(1, priorAgent.OverriddenByAdmin); // a2 used to be

        Assert.Equal(1, Bucket(row, "agent:failed", "Failed").OverriddenOther);
        Assert.Equal(1, Bucket(row, "sweep:r6", "Incomplete").OverriddenByAdmin);
        Assert.Equal(0, Bucket(row, "manual:succeeded", "Succeeded").OverriddenByAdmin);
    }

    [Fact]
    public void ReEnrollment_proxy_counts_only_eligible_terminal_sessions()
    {
        var old = Now.AddDays(-20);
        var young = Now.AddDays(-2);
        var sessions = new List<SessionSummary>
        {
            // old, re-enrolled 3 days later → eligible + re-enrolled
            Session(TenantA, "a1", SessionStatus.Incomplete, old, path: "sweep:r6", completed: old.AddHours(5), serial: "SN-A"),
            // old, next attempt 10 days later → eligible, not re-enrolled
            Session(TenantA, "a2", SessionStatus.Failed, old, path: "agent:failed", completed: old.AddHours(1), serial: "SN-B"),
            // old, no next attempt → eligible, not re-enrolled
            Session(TenantA, "a3", SessionStatus.Succeeded, old, path: "agent:complete", completed: old.AddHours(1), serial: "SN-C"),
            // too young to judge → not eligible even though the chain shows a follow-up
            Session(TenantA, "a4", SessionStatus.Failed, young, path: "agent:failed", completed: young.AddHours(1), serial: "SN-D"),
            // non-terminal → never eligible
            Session(TenantA, "a5", SessionStatus.AwaitingUser, old, path: "sweep:r5_awaiting", serial: "SN-E"),
        };
        var histories = new Dictionary<string, List<DeviceHistory>>
        {
            [TenantA] = new()
            {
                History(TenantA, "SN-A", ("a1", old, old.AddHours(5), "Incomplete"), ("x1", old.AddDays(3), old.AddDays(3).AddHours(1), "Succeeded")),
                History(TenantA, "SN-B", ("a2", old, old.AddHours(1), "Failed"), ("x2", old.AddDays(10), null, "Succeeded")),
                History(TenantA, "SN-C", ("a3", old, old.AddHours(1), "Succeeded")),
                History(TenantA, "SN-D", ("a4", young, young.AddHours(1), "Failed"), ("x4", young.AddHours(2), null, "Succeeded")),
            },
        };
        var rows = MaintenanceService.BuildVerdictCalibrationAggregates(sessions, histories, Now);

        var oldRow = rows.Single(r => r.TenantId == TenantA && r.Date == old.ToString("yyyy-MM-dd"));
        var r6 = Bucket(oldRow, "sweep:r6", "Incomplete");
        Assert.Equal((1, 1), (r6.Eligible7d, r6.ReEnrolled7d));
        var failed = Bucket(oldRow, "agent:failed", "Failed");
        Assert.Equal((1, 0), (failed.Eligible7d, failed.ReEnrolled7d));
        var complete = Bucket(oldRow, "agent:complete", "Succeeded");
        Assert.Equal((1, 0), (complete.Eligible7d, complete.ReEnrolled7d));
        var awaiting = Bucket(oldRow, "sweep:r5_awaiting", "AwaitingUser");
        Assert.Equal((0, 0), (awaiting.Eligible7d, awaiting.ReEnrolled7d));

        var youngRow = rows.Single(r => r.TenantId == TenantA && r.Date == young.ToString("yyyy-MM-dd"));
        var youngFailed = Bucket(youngRow, "agent:failed", "Failed");
        Assert.Equal((0, 0), (youngFailed.Eligible7d, youngFailed.ReEnrolled7d));
    }

    [Fact]
    public void Sessions_inside_a_deletion_cascade_are_skipped()
    {
        var d = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var deleting = Session(TenantA, "a1", SessionStatus.Succeeded, d, path: "agent:complete");
        deleting.DeletionState = "Pending";
        var rows = MaintenanceService.BuildVerdictCalibrationAggregates(new[] { deleting }, new Dictionary<string, List<DeviceHistory>>(), Now);
        Assert.Empty(rows);
    }

    // ---- Persistence round-trip ----

    [Fact]
    public void Aggregate_round_trips_through_the_table_entity()
    {
        var aggregate = new VerdictCalibrationDailyAggregate
        {
            TenantId = TenantA, Date = "2026-08-10", Version = 1, SessionCount = 5, TerminalSessionCount = 4, ComputedAt = Now,
            Buckets =
            {
                new VerdictCalibrationBucket { VerdictPath = "agent:complete", Status = "Succeeded", Count = 3, DerivedCount = 1, Eligible7d = 3, ReEnrolled7d = 1, OverriddenByAdmin = 1, OverriddenByLateCompletion = 0, OverriddenOther = 2 },
                new VerdictCalibrationBucket { VerdictPath = "sweep:r6", Status = "Incomplete", Count = 1 },
            },
        };
        var entity = TableStorageService.BuildVerdictCalibrationAggregateEntity(aggregate);
        Assert.Equal(TenantA, entity.PartitionKey);
        Assert.Equal("2026-08-10", entity.RowKey);

        var back = TableStorageService.MapToVerdictCalibrationAggregate(entity);
        Assert.Equal(JsonSerializer.Serialize(aggregate), JsonSerializer.Serialize(back));
    }

    // ---- Response builder ----

    private static VerdictCalibrationDailyAggregate Day(string tenant, DateTime date, int sessions, params (string Path, string Status, int Count)[] buckets) => new()
    {
        TenantId = tenant, Date = date.ToString("yyyy-MM-dd"), SessionCount = sessions, TerminalSessionCount = sessions, Version = 1, ComputedAt = Now,
        Buckets = buckets.Select(b => new VerdictCalibrationBucket { VerdictPath = b.Path, Status = b.Status, Count = b.Count, Eligible7d = b.Count, ReEnrolled7d = b.Count / 5 }).ToList(),
    };

    // Same naming policy as the worker host (Program.cs: camelCase) so the assertions pin the wire shape.
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static JsonElement BuildJson(IReadOnlyList<VerdictCalibrationDailyAggregate> daily, DateTime today, int days)
        => JsonSerializer.SerializeToElement(VerdictCalibrationResponseBuilder.Build(daily, "global", today, days), Wire);

    [Fact]
    public void Response_sums_the_window_and_computes_share_rate_and_trend()
    {
        var today = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
        var daily = new List<VerdictCalibrationDailyAggregate>();
        // 35 days: baseline days carry 10 sessions (1 sweep:r6), the last 7 days carry 10 sessions (3 sweep:r6) → share 10% → 30%, lift 3.0
        for (var i = 34; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var r6 = i < 7 ? 3 : 1;
            daily.Add(Day("global", date, 10, ("agent:complete", "Succeeded", 10 - r6), ("sweep:r6", "Incomplete", r6)));
        }
        var json = BuildJson(daily, today, days: 30);

        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(30, json.GetProperty("windowDays").GetInt32());
        Assert.Equal(300, json.GetProperty("totals").GetProperty("sessions").GetInt32());
        Assert.Equal(30, json.GetProperty("totals").GetProperty("days").GetInt32());

        var paths = json.GetProperty("paths").EnumerateArray().ToList();
        var r6Row = paths.Single(p => p.GetProperty("verdictPath").GetString() == "sweep:r6");
        Assert.Equal(7 * 3 + 23 * 1, r6Row.GetProperty("count").GetInt32());
        Assert.Equal(Math.Round(100.0 * 44 / 300, 1), r6Row.GetProperty("sharePct").GetDouble());
        Assert.Equal(30.0, r6Row.GetProperty("window7").GetProperty("sharePct").GetDouble());
        Assert.Equal(10.0, r6Row.GetProperty("baseline28").GetProperty("sharePct").GetDouble());
        Assert.Equal(3.0, r6Row.GetProperty("lift").GetDouble());
        // Eligible 44 ≥ 20 → rate stated; ReEnrolled7d = count/5 per day → 7*0 + 23*0 = 0 → 0%
        Assert.Equal(0.0, r6Row.GetProperty("reEnrollRatePct").GetDouble());

        // Sorted by count desc → agent:complete first.
        Assert.Equal("agent:complete", paths[0].GetProperty("verdictPath").GetString());
    }

    [Fact]
    public void Response_withholds_rate_below_twenty_eligible_and_lift_without_baseline()
    {
        var today = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
        var daily = new List<VerdictCalibrationDailyAggregate>
        {
            Day("global", today, 5, ("maxlife:r3", "Incomplete", 5)),
        };
        var json = BuildJson(daily, today, days: 7);
        var row = json.GetProperty("paths").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, row.GetProperty("reEnrollRatePct").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lift").ValueKind); // no baseline rows → never invented
        Assert.Equal(100.0, row.GetProperty("sharePct").GetDouble());
    }

    [Fact]
    public void Response_keeps_a_zero_count_bucket_that_carries_overrides()
    {
        var today = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
        var day = Day("global", today, 1, ("agent:complete", "Succeeded", 1));
        day.Buckets.Add(new VerdictCalibrationBucket { VerdictPath = "sweep:r5_incomplete", Status = "Incomplete", Count = 0, OverriddenByLateCompletion = 1 });
        var json = BuildJson(new[] { day }, today, days: 7);
        var rows = json.GetProperty("paths").EnumerateArray().ToList();
        var prior = rows.Single(p => p.GetProperty("verdictPath").GetString() == "sweep:r5_incomplete");
        Assert.Equal(0, prior.GetProperty("count").GetInt32());
        Assert.Equal(1, prior.GetProperty("overriddenByLateCompletion").GetInt32());
    }

    [Fact]
    public void Response_is_empty_but_well_formed_before_the_first_sweep()
    {
        var json = BuildJson(Array.Empty<VerdictCalibrationDailyAggregate>(), new DateTime(2026, 8, 23), 30);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Empty(json.GetProperty("paths").EnumerateArray());
        Assert.Equal(0, json.GetProperty("totals").GetProperty("sessions").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("computedAt").ValueKind);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("abc", 30)]
    [InlineData("0", 1)]
    [InlineData("500", 180)]
    [InlineData("14", 14)]
    public void ClampDays_bounds_the_window(string? raw, int expected)
        => Assert.Equal(expected, VerdictCalibrationResponseBuilder.ClampDays(raw));
}
