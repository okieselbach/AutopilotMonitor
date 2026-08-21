using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// F1 PR2 (insights spec §F1 "Data &amp; compute changes"): persistence round-trips for the
/// SessionTimeBreakdowns / TimeAttributionAggregates rows (table-storage-serialization rule —
/// every property must survive Store→Map, tri-states included), the pure daily-aggregation
/// core (<see cref="MaintenanceService.BuildTimeAttributionAggregates"/>: clean/flagged/missing
/// disclosure, per-class bucketing, fixed segment stack, per-app gates) and the what-if bound.
/// </summary>
public class TimeAttributionPersistenceAndAggregationTests
{
    private static readonly DateTime T0 = new(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
    private const string TenantA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string TenantB = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string AppX = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string AppY = "bbbbbbbb-1111-2222-3333-444444444444";

    // ── breakdown row round-trip ────────────────────────────────────────────

    [Fact]
    public void BreakdownEntity_RoundTripsAllFields_IncludingJsonLists()
    {
        var breakdown = new SessionTimeBreakdown
        {
            TenantId = TenantA,
            SessionId = "s-1",
            AttributionVersion = 1,
            EventCountAtCompute = 261,
            WallClockSeconds = 1800,
            UnattributedSeconds = 30,
            RebootSeconds = 240,
            BlockingAppCount = 3,
            EspAppsOccupancySeconds = 600,
            QualityFlags = TimeAttributionFlags.PartialObservation | TimeAttributionFlags.BlockingSetTruncated,
            Segments = new List<TimeAttributionSpan>
            {
                new() { SegmentKey = TimeAttributionSegments.DevicePrep, StartUtc = T0, EndUtc = T0.AddMinutes(5), Seconds = 300 },
                new() { SegmentKey = TimeAttributionSegments.EspApps, StartUtc = T0.AddMinutes(5), EndUtc = T0.AddMinutes(30), Seconds = 1500 },
            },
            RebootSpans = new List<RebootSpan>
            {
                new() { StartUtc = T0.AddMinutes(10), EndUtc = T0.AddMinutes(14), Seconds = 240, SegmentKey = TimeAttributionSegments.EspApps },
            },
            SleepSeconds = 360,
            SleepSpans = new List<SleepSpan>
            {
                new() { StartUtc = T0.AddMinutes(20), EndUtc = T0.AddMinutes(26), Seconds = 360, SegmentKey = TimeAttributionSegments.EspApps, Kind = "modern_standby" },
            },
            BlockingApps = new List<BlockingAppInterval>
            {
                new() { AppId = AppX, AppName = "App X", StartUtc = T0.AddMinutes(6), EndUtc = T0.AddMinutes(16), Seconds = 600 },
            },
        };

        var mapped = TableStorageService.MapToSessionTimeBreakdown(
            TableStorageService.BuildSessionTimeBreakdownEntity(breakdown));

        Assert.Equal(TenantA, mapped.TenantId);
        Assert.Equal("s-1", mapped.SessionId);
        Assert.Equal(1, mapped.AttributionVersion);
        // Sweep change signal (Codex review: late batches must trigger a recompute) — a lost
        // column would read as 0 and recompute forever OR mask real drift.
        Assert.Equal(261, mapped.EventCountAtCompute);
        Assert.Equal(1800, mapped.WallClockSeconds);
        Assert.Equal(30, mapped.UnattributedSeconds);
        Assert.Equal(240, mapped.RebootSeconds);
        Assert.Equal(3, mapped.BlockingAppCount);
        Assert.Equal(600, mapped.EspAppsOccupancySeconds);
        Assert.Equal(TimeAttributionFlags.PartialObservation | TimeAttributionFlags.BlockingSetTruncated, mapped.QualityFlags);

        Assert.Equal(2, mapped.Segments.Count);
        Assert.Equal(TimeAttributionSegments.DevicePrep, mapped.Segments[0].SegmentKey);
        Assert.Equal(T0, mapped.Segments[0].StartUtc);
        Assert.Equal(300, mapped.Segments[0].Seconds);

        var reboot = Assert.Single(mapped.RebootSpans);
        Assert.Equal(TimeAttributionSegments.EspApps, reboot.SegmentKey);
        Assert.Equal(240, reboot.Seconds);

        Assert.Equal(360, mapped.SleepSeconds);
        var sleep = Assert.Single(mapped.SleepSpans);
        Assert.Equal("modern_standby", sleep.Kind);
        Assert.Equal(TimeAttributionSegments.EspApps, sleep.SegmentKey);
        Assert.Equal(T0.AddMinutes(20), sleep.StartUtc);
        Assert.Equal(360, sleep.Seconds);

        var app = Assert.Single(mapped.BlockingApps);
        Assert.Equal(AppX, app.AppId);
        Assert.Equal("App X", app.AppName);
        Assert.Equal(600, app.Seconds);
    }

    [Fact]
    public void BreakdownEntity_OccupancyTriState_AbsentColumnMapsToNull_NeverZero()
    {
        var unknown = new SessionTimeBreakdown
        {
            TenantId = TenantA, SessionId = "s-1", WallClockSeconds = 600,
            EspAppsOccupancySeconds = null, QualityFlags = TimeAttributionFlags.BlockingSetUnknown,
        };

        var entity = TableStorageService.BuildSessionTimeBreakdownEntity(unknown);
        Assert.False(entity.ContainsKey("EspAppsOccupancySeconds"));
        Assert.Null(TableStorageService.MapToSessionTimeBreakdown(entity).EspAppsOccupancySeconds);

        // Zero is a real measured value (blocking set known, nothing occupied) — must survive.
        var zero = new SessionTimeBreakdown
        {
            TenantId = TenantA, SessionId = "s-2", WallClockSeconds = 600, EspAppsOccupancySeconds = 0,
        };
        Assert.Equal(0, TableStorageService.MapToSessionTimeBreakdown(
            TableStorageService.BuildSessionTimeBreakdownEntity(zero)).EspAppsOccupancySeconds);
    }

    // ── aggregate row round-trip ────────────────────────────────────────────

    [Fact]
    public void AggregateEntity_RoundTripsAllFields()
    {
        var aggregate = new TimeAttributionDailyAggregate
        {
            TenantId = TenantA,
            Date = "2026-07-26",
            EnrollmentClass = "user_driven",
            AttributionVersion = 1,
            CleanSessionCount = 23,
            FlaggedExcludedCount = 4,
            MissingBreakdownCount = 1,
            ComputedAt = T0,
            SegmentStats = new List<TimeAttributionSegmentStat>
            {
                new() { SegmentKey = TimeAttributionSegments.EspApps, MedianSeconds = 600, P75Seconds = 700, P90Seconds = 900 },
            },
            TopBlockingApps = new List<TimeAttributionBlockingAppStat>
            {
                new()
                {
                    AppId = AppX, AppName = "App X", SessionCount = 12,
                    MedianSeconds = 300, P75Seconds = 400, MedianSavingSeconds = 120, P75SavingSeconds = 200,
                },
            },
        };

        var entity = TableStorageService.BuildTimeAttributionAggregateEntity(aggregate);
        Assert.Equal("2026-07-26|user_driven", entity.RowKey);

        var mapped = TableStorageService.MapToTimeAttributionAggregate(entity);
        Assert.Equal(TenantA, mapped.TenantId);
        Assert.Equal("2026-07-26", mapped.Date);
        Assert.Equal("user_driven", mapped.EnrollmentClass);
        Assert.Equal(1, mapped.AttributionVersion);
        Assert.Equal(23, mapped.CleanSessionCount);
        Assert.Equal(4, mapped.FlaggedExcludedCount);
        Assert.Equal(1, mapped.MissingBreakdownCount);

        var segment = Assert.Single(mapped.SegmentStats);
        Assert.Equal(TimeAttributionSegments.EspApps, segment.SegmentKey);
        Assert.Equal(600, segment.MedianSeconds);
        Assert.Equal(900, segment.P90Seconds);

        var app = Assert.Single(mapped.TopBlockingApps);
        Assert.Equal(AppX, app.AppId);
        Assert.Equal(12, app.SessionCount);
        Assert.Equal(120, app.MedianSavingSeconds);
    }

    // ── enrollment class ────────────────────────────────────────────────────

    [Fact]
    public void EnrollmentClass_Precedence_WhiteGlove_Wdp_SelfDeploying_UserDriven()
    {
        Assert.Equal("whiteglove", TimeAttributionCalculator.GetEnrollmentClass(
            new SessionSummary { IsPreProvisioned = true, EnrollmentType = "v2", IsSelfDeployingProfile = true }));
        Assert.Equal("device_preparation", TimeAttributionCalculator.GetEnrollmentClass(
            new SessionSummary { EnrollmentType = "v2", IsSelfDeployingProfile = true }));
        Assert.Equal("self_deploying", TimeAttributionCalculator.GetEnrollmentClass(
            new SessionSummary { IsSelfDeployingProfile = true }));
        Assert.Equal("user_driven", TimeAttributionCalculator.GetEnrollmentClass(new SessionSummary()));
    }

    // ── what-if bound ───────────────────────────────────────────────────────

    private static BlockingAppInterval Interval(string appId, DateTime start, DateTime end)
        => new() { AppId = appId, AppName = appId, StartUtc = start, EndUtc = end, Seconds = (int)(end - start).TotalSeconds };

    [Fact]
    public void WhatIf_LastEndingApp_SavesTailToPreviousEnd_OthersSaveNothing()
    {
        var intervals = new List<BlockingAppInterval>
        {
            Interval(AppX, T0, T0.AddMinutes(10)),                 // ends 10:10
            Interval(AppY, T0.AddMinutes(5), T0.AddMinutes(20)),   // ends 10:20 — critical-path end
        };

        // Removing Y moves the path end 10:20 → 10:10.
        Assert.Equal(600, TimeAttributionCalculator.WhatIfSavingSeconds(intervals, AppY));
        // Removing X changes nothing — Y still ends at 10:20.
        Assert.Equal(0, TimeAttributionCalculator.WhatIfSavingSeconds(intervals, AppX));
        // An app without a measured interval yields no claim.
        Assert.Equal(0, TimeAttributionCalculator.WhatIfSavingSeconds(intervals, "cccccccc-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void WhatIf_OnlyBlockingApp_FallsBackToOwnStart()
    {
        var intervals = new List<BlockingAppInterval> { Interval(AppX, T0.AddMinutes(5), T0.AddMinutes(20)) };
        Assert.Equal(900, TimeAttributionCalculator.WhatIfSavingSeconds(intervals, AppX));
    }

    [Fact]
    public void WhatIf_IdleGapBeforeLastApp_CountsIntoTheBound()
    {
        // A ends 10:00; X runs 10:05 → 10:20. Removing X exposes 10:00 as the path end —
        // the ESP waited for X to START too, so the bound is 20 min, not 15 (spec formula).
        var intervals = new List<BlockingAppInterval>
        {
            Interval(AppY, T0.AddMinutes(-30), T0),
            Interval(AppX, T0.AddMinutes(5), T0.AddMinutes(20)),
        };
        Assert.Equal(1200, TimeAttributionCalculator.WhatIfSavingSeconds(intervals, AppX));
    }

    // ── daily aggregation core ──────────────────────────────────────────────

    private static SessionSummary Session(string tenantId, DateTime startedAt, bool wg = false)
        => new()
        {
            TenantId = tenantId,
            SessionId = Guid.NewGuid().ToString(),
            StartedAt = startedAt,
            Status = SessionStatus.Succeeded,
            IsPreProvisioned = wg,
        };

    private static SessionTimeBreakdown Breakdown(
        TimeAttributionFlags flags = TimeAttributionFlags.None,
        int espAppsSeconds = 600,
        int unattributed = 0,
        List<BlockingAppInterval>? apps = null)
        => new()
        {
            AttributionVersion = TimeAttributionCalculator.CurrentVersion,
            WallClockSeconds = espAppsSeconds + 300 + unattributed,
            UnattributedSeconds = unattributed,
            QualityFlags = flags,
            Segments = new List<TimeAttributionSpan>
            {
                new() { SegmentKey = TimeAttributionSegments.DevicePrep, StartUtc = T0, EndUtc = T0.AddSeconds(300), Seconds = 300 },
                new() { SegmentKey = TimeAttributionSegments.EspApps, StartUtc = T0.AddSeconds(300), EndUtc = T0.AddSeconds(300 + espAppsSeconds), Seconds = espAppsSeconds },
            },
            BlockingApps = apps ?? new List<BlockingAppInterval>(),
        };

    [Fact]
    public void Aggregates_BucketPerTenantClassDate_AndMirrorGlobal()
    {
        var pairs = new List<(SessionSummary, SessionTimeBreakdown)>
        {
            (Session(TenantA, T0), Breakdown(espAppsSeconds: 100)),
            (Session(TenantA, T0), Breakdown(espAppsSeconds: 300)),
            (Session(TenantA, T0, wg: true), Breakdown(espAppsSeconds: 900)),   // separate class
            (Session(TenantB, T0), Breakdown(espAppsSeconds: 500)),             // separate tenant
        };

        var aggregates = MaintenanceService.BuildTimeAttributionAggregates(
            pairs, Array.Empty<SessionSummary>(), T0);

        // Tenant A: user_driven + whiteglove; Tenant B: user_driven; global: user_driven + whiteglove.
        Assert.Equal(5, aggregates.Count);

        var tenantAUde = aggregates.Single(a => a.TenantId == TenantA && a.EnrollmentClass == "user_driven");
        Assert.Equal("2026-07-26", tenantAUde.Date);
        Assert.Equal(2, tenantAUde.CleanSessionCount);
        Assert.Equal(TimeAttributionCalculator.CurrentVersion, tenantAUde.AttributionVersion);
        // Median of {100, 300} = 100 (nearest-rank: ceil(0.5·2)−1 = index 0 — repo convention).
        Assert.Equal(100, tenantAUde.SegmentStats.Single(s => s.SegmentKey == TimeAttributionSegments.EspApps).MedianSeconds);

        var wg = aggregates.Single(a => a.TenantId == TenantA && a.EnrollmentClass == "whiteglove");
        Assert.Equal(1, wg.CleanSessionCount); // classes never mixed

        var globalUde = aggregates.Single(a => a.TenantId == "global" && a.EnrollmentClass == "user_driven");
        Assert.Equal(3, globalUde.CleanSessionCount); // A(2) + B(1)
    }

    [Fact]
    public void Aggregates_FlaggedSessions_AreExcludedFromStats_ButDisclosed()
    {
        var pairs = new List<(SessionSummary, SessionTimeBreakdown)>
        {
            (Session(TenantA, T0), Breakdown(espAppsSeconds: 100)),
            (Session(TenantA, T0), Breakdown(flags: TimeAttributionFlags.PartialObservation, espAppsSeconds: 99999)),
        };

        var aggregate = MaintenanceService.BuildTimeAttributionAggregates(
                pairs, Array.Empty<SessionSummary>(), T0)
            .Single(a => a.TenantId == TenantA);

        Assert.Equal(1, aggregate.CleanSessionCount);
        Assert.Equal(1, aggregate.FlaggedExcludedCount);
        // The flagged 99999s outlier must not shape the median.
        Assert.Equal(100, aggregate.SegmentStats.Single(s => s.SegmentKey == TimeAttributionSegments.EspApps).MedianSeconds);
    }

    [Fact]
    public void Aggregates_AllFlaggedDay_YieldsDisclosureRow_WithoutStats()
    {
        var pairs = new List<(SessionSummary, SessionTimeBreakdown)>
        {
            (Session(TenantA, T0), Breakdown(flags: TimeAttributionFlags.ClockSkewDropped)),
        };

        var aggregate = MaintenanceService.BuildTimeAttributionAggregates(
                pairs, Array.Empty<SessionSummary>(), T0)
            .Single(a => a.TenantId == TenantA);

        Assert.Equal(0, aggregate.CleanSessionCount);
        Assert.Equal(1, aggregate.FlaggedExcludedCount);
        Assert.Empty(aggregate.SegmentStats);   // no clean sessions → no statistical claim
        Assert.Empty(aggregate.TopBlockingApps);
    }

    [Fact]
    public void Aggregates_BlockingOnlyFlags_StayInSegmentStats()
    {
        // BlockingSetUnknown/Truncated limit per-app blocking EVIDENCE, not the measured spans.
        // Gating on any flag starved the fleet aggregates in production (903/1000 breakdowns
        // carried BlockingSetUnknown, 2026-07-27) — blocking-only flagged sessions must count
        // as clean; only duration-critical flags exclude.
        var pairs = new List<(SessionSummary, SessionTimeBreakdown)>
        {
            (Session(TenantA, T0), Breakdown(flags: TimeAttributionFlags.BlockingSetUnknown, espAppsSeconds: 100)),
            (Session(TenantA, T0), Breakdown(flags: TimeAttributionFlags.BlockingSetTruncated, espAppsSeconds: 300)),
            (Session(TenantA, T0), Breakdown(
                flags: TimeAttributionFlags.BlockingSetUnknown | TimeAttributionFlags.PartialObservation,
                espAppsSeconds: 99999)), // duration-critical bit set → still excluded
        };

        var aggregate = MaintenanceService.BuildTimeAttributionAggregates(
                pairs, Array.Empty<SessionSummary>(), T0)
            .Single(a => a.TenantId == TenantA);

        Assert.Equal(2, aggregate.CleanSessionCount);
        Assert.Equal(1, aggregate.FlaggedExcludedCount);
        // Median of {100, 300} = 100 (nearest-rank, lower value — repo convention).
        Assert.Equal(100, aggregate.SegmentStats.Single(s => s.SegmentKey == TimeAttributionSegments.EspApps).MedianSeconds);
    }

    [Fact]
    public void Aggregates_MissingBreakdowns_AreCounted_NeverGuessed()
    {
        var missing = new List<SessionSummary> { Session(TenantA, T0) };

        var aggregate = MaintenanceService.BuildTimeAttributionAggregates(
                Array.Empty<(SessionSummary, SessionTimeBreakdown)>(), missing, T0)
            .Single(a => a.TenantId == TenantA);

        Assert.Equal(0, aggregate.CleanSessionCount);
        Assert.Equal(1, aggregate.MissingBreakdownCount);
        Assert.Empty(aggregate.SegmentStats);
    }

    [Fact]
    public void Aggregates_SegmentStack_AlwaysCarriesAllSegmentsPlusUnattributed_AbsentAsZero()
    {
        var pairs = new List<(SessionSummary, SessionTimeBreakdown)>
        {
            (Session(TenantA, T0), Breakdown(unattributed: 42)),
        };

        var aggregate = MaintenanceService.BuildTimeAttributionAggregates(
                pairs, Array.Empty<SessionSummary>(), T0)
            .Single(a => a.TenantId == TenantA);

        Assert.Equal(6, aggregate.SegmentStats.Count); // 5 canonical + unattributed — the full stack
        // The fixture has no user_esp span → honest 0, not absent.
        Assert.Equal(0, aggregate.SegmentStats.Single(s => s.SegmentKey == TimeAttributionSegments.UserEsp).MedianSeconds);
        Assert.Equal(42, aggregate.SegmentStats.Single(s => s.SegmentKey == TimeAttributionSegments.Unattributed).MedianSeconds);
    }

    [Fact]
    public void Aggregates_RollingDateKey_MergesAllDatesIntoRangeStatistics()
    {
        // Two sessions on DIFFERENT days — the rolling row must merge them (a median of
        // per-day medians would not be the range median; the rolling rows are the honest
        // range statistics the fleet panel reads).
        var pairs = new List<(SessionSummary, SessionTimeBreakdown)>
        {
            (Session(TenantA, T0), Breakdown(espAppsSeconds: 100)),
            (Session(TenantA, T0.AddDays(-3)), Breakdown(espAppsSeconds: 300)),
        };

        var rolling = MaintenanceService.BuildTimeAttributionAggregates(
            pairs, Array.Empty<SessionSummary>(), T0, MaintenanceService.RollingAggregateDateKey);

        var row = rolling.Single(a => a.TenantId == TenantA);
        Assert.Equal(MaintenanceService.RollingAggregateDateKey, row.Date);
        Assert.Equal(2, row.CleanSessionCount);
        // Rolling rows sort AFTER every "yyyy-MM-dd|…" key so date-range reads and the
        // age-based retention filter never touch them.
        Assert.True(string.CompareOrdinal($"{row.Date}|user_driven", "2026-12-31|zzz") > 0);
    }

    [Fact]
    public void Aggregates_PerAppRows_GateAtFiveSessions_AndCarryWhatIfBounds()
    {
        var pairs = new List<(SessionSummary, SessionTimeBreakdown)>();
        // App X in 5 sessions (ends last each time → saving = its full tail over device-prep-only baseline).
        for (var i = 0; i < 5; i++)
        {
            pairs.Add((Session(TenantA, T0), Breakdown(apps: new List<BlockingAppInterval>
            {
                Interval(AppX, T0.AddMinutes(5), T0.AddMinutes(15)), // only measured blocking app → saving 600
            })));
        }
        // App Y in only 4 sessions → below the per-app row gate.
        for (var i = 0; i < 4; i++)
        {
            pairs.Add((Session(TenantA, T0), Breakdown(apps: new List<BlockingAppInterval>
            {
                Interval(AppY, T0.AddMinutes(5), T0.AddMinutes(6)),
            })));
        }

        var aggregate = MaintenanceService.BuildTimeAttributionAggregates(
                pairs, Array.Empty<SessionSummary>(), T0)
            .Single(a => a.TenantId == TenantA);

        var row = Assert.Single(aggregate.TopBlockingApps);
        Assert.Equal(AppX, row.AppId);
        Assert.Equal(5, row.SessionCount);
        Assert.Equal(600, row.MedianSeconds);
        Assert.Equal(600, row.MedianSavingSeconds); // sole blocking app → bound = full interval
    }

    // ── stale-bucket reconcile key consistency (Codex review) ───────────────

    [Fact]
    public void RollingAggregateEntity_RowKey_MatchesTargetedDeleteKey()
    {
        // The sweep's reconcile deletes by (Date, EnrollmentClass) — the entity builder and the
        // delete path must agree on the RK shape for BOTH daily and rolling rows, or stale
        // rolling rows would never be removed.
        var rolling = new TimeAttributionDailyAggregate
        {
            TenantId = TenantA, Date = "rolling30", EnrollmentClass = "whiteglove",
        };
        Assert.Equal("rolling30|whiteglove", TableStorageService.BuildTimeAttributionAggregateEntity(rolling).RowKey);

        var daily = new TimeAttributionDailyAggregate
        {
            TenantId = TenantA, Date = "2026-07-27", EnrollmentClass = "user_driven",
        };
        Assert.Equal("2026-07-27|user_driven", TableStorageService.BuildTimeAttributionAggregateEntity(daily).RowKey);
    }

    [Fact]
    public void TimeAttribution_InclusiveWindowStart_YieldsExactly30CalendarDays()
    {
        // Codex review: both range ends are inclusive — subtracting the full WindowDays
        // returned 31 day keys under a "windowDays: 30" label.
        var today = new System.DateTime(2026, 7, 27);
        Assert.Equal(today.AddDays(-29),
            Functions.Metrics.TimeAttributionResponse.InclusiveWindowStart(today, Functions.Metrics.TimeAttributionResponse.WindowDays));
    }
}
