using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verdict-calibration drift radar (docs/backend/verdict-calibration.md): the three finding
/// kinds, the small-n / lift / Wilson gates, re-arm, and the tracker round-trip.
/// </summary>
public class VerdictCalibrationRadarTests
{
    private static readonly DateTime Target = new(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>35 daily rows ending on Target; per-day buckets chosen by window/baseline membership.</summary>
    private static List<VerdictCalibrationDailyAggregate> Horizon(
        Func<bool, (string Path, string Status, int Count)[]> bucketsFor, int sessionsPerDay = 20)
    {
        var rows = new List<VerdictCalibrationDailyAggregate>();
        for (var i = 34; i >= 0; i--)
        {
            var inWindow = i < 7;
            var date = Target.AddDays(-i);
            rows.Add(new VerdictCalibrationDailyAggregate
            {
                TenantId = "t", Date = date.ToString("yyyy-MM-dd"), SessionCount = sessionsPerDay, TerminalSessionCount = sessionsPerDay,
                Buckets = bucketsFor(inWindow).Select(b => new VerdictCalibrationBucket { VerdictPath = b.Path, Status = b.Status, Count = b.Count }).ToList(),
            });
        }
        return rows;
    }

    [Fact]
    public void Share_regression_fires_on_a_doubled_separated_share()
    {
        // baseline: 1/20 per day (5%), window: 4/20 per day (20%) → lift 4, clearly separated
        var rows = Horizon(w => new[] { ("sweep:r5_incomplete", "Incomplete", w ? 4 : 1), ("agent:complete", "Succeeded", w ? 16 : 19) });
        var findings = VerdictCalibrationRadar.Evaluate(rows, Target);

        var f = Assert.Single(findings, x => x.Kind == VerdictCalibrationAlertKinds.ShareRegression);
        Assert.Equal("sweep:r5_incomplete", f.VerdictPath);
        Assert.Equal("Incomplete", f.Status);
        Assert.Equal(28, f.WindowHitCount);
        Assert.Equal(140, f.WindowSessionCount);
        Assert.Equal(28, f.BaselineHitCount);
        Assert.Equal(560, f.BaselineSessionCount);
        Assert.Equal(20.0, f.WindowRatePct);
        Assert.Equal(5.0, f.BaselineRatePct);
        Assert.Equal(4.0, f.Lift);
        Assert.Equal("2026-08-16", f.WindowStartDate);
        Assert.Equal("2026-08-22", f.WindowEndDate);
        // sweep:* is also the silence group → silence share doubled as well
        Assert.Contains(findings, x => x.Kind == VerdictCalibrationAlertKinds.SilenceShareRegression && x.VerdictPath == VerdictCalibrationRadar.SilenceGroupPath);
    }

    [Fact]
    public void Share_regression_is_suppressed_below_lift_two_or_without_separation()
    {
        // 5% → 8%: lift 1.6 — under the gate
        var mild = Horizon(w => new[] { ("agent:failed", "Failed", w ? 2 : 1), ("agent:complete", "Succeeded", w ? 18 : 19) }, sessionsPerDay: 20);
        Assert.DoesNotContain(VerdictCalibrationRadar.Evaluate(mild, Target), f => f.Kind == VerdictCalibrationAlertKinds.ShareRegression);

        // tiny n: 1/2 per day in window vs 0/2 baseline — lift infinite but Wilson intervals overlap / hits < 5
        var tiny = Horizon(w => new[] { ("agent:failed", "Failed", w ? 1 : 0), ("agent:complete", "Succeeded", w ? 1 : 2) }, sessionsPerDay: 2);
        Assert.Empty(VerdictCalibrationRadar.Evaluate(tiny, Target));
    }

    [Fact]
    public void Agent_and_registration_paths_never_share_alert_but_still_feed_the_silence_group()
    {
        // Workflow mix, not classifier drift: a WhiteGlove rollout week doubles the pending share.
        var wg = Horizon(w => new[] { ("agent:whiteglove_pending", "Pending", w ? 6 : 1), ("agent:complete", "Succeeded", w ? 14 : 19) });
        Assert.DoesNotContain(VerdictCalibrationRadar.Evaluate(wg, Target), f => f.Kind == VerdictCalibrationAlertKinds.ShareRegression);
        var reg = Horizon(w => new[] { ("register:superseded", "Incomplete", w ? 6 : 1), ("agent:complete", "Succeeded", w ? 14 : 19) });
        Assert.DoesNotContain(VerdictCalibrationRadar.Evaluate(reg, Target), f => f.Kind == VerdictCalibrationAlertKinds.ShareRegression);
        // Backend-decided paths stay eligible.
        Assert.True(VerdictCalibrationRadar.IsShareRegressionEligible("sweep:r6"));
        Assert.True(VerdictCalibrationRadar.IsShareRegressionEligible("rule:ANALYZE-ESP-001"));
        Assert.True(VerdictCalibrationRadar.IsShareRegressionEligible("manual:failed"));
        Assert.False(VerdictCalibrationRadar.IsShareRegressionEligible("agent:complete_soft"));

        // An episode opened by the earlier, broader gate re-arms on the next pass regardless of numbers.
        var stale = new VerdictCalibrationAlert { Kind = VerdictCalibrationAlertKinds.ShareRegression, VerdictPath = "agent:whiteglove_pending", Status = "Pending" };
        Assert.True(VerdictCalibrationRadar.ShouldReArm(stale, wg, Target));
    }

    [Fact]
    public void Derived_legacy_paths_never_alert()
    {
        var rows = Horizon(w => new[] { ("legacy:r6", "Incomplete", w ? 10 : 1), ("agent:complete", "Succeeded", w ? 10 : 19) });
        Assert.DoesNotContain(VerdictCalibrationRadar.Evaluate(rows, Target), f => f.Kind == VerdictCalibrationAlertKinds.ShareRegression);
    }

    [Fact]
    public void Evidence_gap_fires_on_r6_share_and_ignores_non_rule_sweep_paths()
    {
        // classifier verdicts in window: r6 5/day + r5 15/day = 140; r6 share 25% → fires. sweep:stalled is not a rule verdict.
        var rows = Horizon(w => new[]
        {
            ("sweep:r6", "Incomplete", 5), ("sweep:r5_incomplete", "Incomplete", 15), ("sweep:stalled", "Stalled", 30),
        }, sessionsPerDay: 50);
        var f = Assert.Single(VerdictCalibrationRadar.Evaluate(rows, Target), x => x.Kind == VerdictCalibrationAlertKinds.EvidenceGap);
        Assert.Equal(VerdictCalibrationRadar.EvidenceGapPath, f.VerdictPath);
        Assert.Equal(35, f.WindowHitCount);
        Assert.Equal(140, f.WindowSessionCount);
        Assert.Equal(25.0, f.WindowRatePct);
        Assert.Null(f.Lift);
    }

    [Fact]
    public void Evidence_gap_needs_twenty_classifier_verdicts()
    {
        var rows = Horizon(w => new[] { ("sweep:r6", "Incomplete", w ? 2 : 0), ("agent:complete", "Succeeded", 18) });
        Assert.DoesNotContain(VerdictCalibrationRadar.Evaluate(rows, Target), f => f.Kind == VerdictCalibrationAlertKinds.EvidenceGap);
    }

    [Fact]
    public void ReArm_when_share_falls_under_one_point_five_or_path_stops()
    {
        var alert = new VerdictCalibrationAlert { Kind = VerdictCalibrationAlertKinds.ShareRegression, VerdictPath = "sweep:r6", Status = "Incomplete" };

        var stillHigh = Horizon(w => new[] { ("sweep:r6", "Incomplete", w ? 4 : 1), ("agent:complete", "Succeeded", w ? 16 : 19) });
        Assert.False(VerdictCalibrationRadar.ShouldReArm(alert, stillHigh, Target));

        var elevatedButUnderGate = Horizon(w => new[] { ("sweep:r6", "Incomplete", w ? 2 : 1), ("agent:complete", "Succeeded", w ? 18 : 19) }, sessionsPerDay: 20);
        // 10% vs 5% = 2.0× — still at/above 1.5× → keep
        Assert.False(VerdictCalibrationRadar.ShouldReArm(alert, elevatedButUnderGate, Target));

        var back = Horizon(w => new[] { ("sweep:r6", "Incomplete", 1), ("agent:complete", "Succeeded", 19) });
        Assert.True(VerdictCalibrationRadar.ShouldReArm(alert, back, Target));

        var gone = Horizon(w => new[] { ("sweep:r6", "Incomplete", w ? 0 : 1), ("agent:complete", "Succeeded", w ? 20 : 19) });
        Assert.True(VerdictCalibrationRadar.ShouldReArm(alert, gone, Target));
    }

    [Fact]
    public void Evidence_gap_rearms_under_fifteen_percent()
    {
        var alert = new VerdictCalibrationAlert { Kind = VerdictCalibrationAlertKinds.EvidenceGap, VerdictPath = "r6", Status = "*" };
        var still = Horizon(w => new[] { ("sweep:r6", "Incomplete", 4), ("sweep:r5_incomplete", "Incomplete", 16) });
        Assert.False(VerdictCalibrationRadar.ShouldReArm(alert, still, Target)); // 20%
        var between = Horizon(w => new[] { ("sweep:r6", "Incomplete", 17), ("sweep:r5_incomplete", "Incomplete", 83) }, sessionsPerDay: 100);
        Assert.False(VerdictCalibrationRadar.ShouldReArm(alert, between, Target)); // 17%
        var low = Horizon(w => new[] { ("sweep:r6", "Incomplete", 1), ("sweep:r5_incomplete", "Incomplete", 19) });
        Assert.True(VerdictCalibrationRadar.ShouldReArm(alert, low, Target)); // 5%
    }

    [Fact]
    public void CurrentSums_follow_the_alert_kind()
    {
        var rows = Horizon(w => new[] { ("sweep:r6", "Incomplete", 2), ("maxlife:r3", "Incomplete", 1), ("agent:complete", "Succeeded", 17) });
        var share = VerdictCalibrationRadar.CurrentSums(new VerdictCalibrationAlert { Kind = VerdictCalibrationAlertKinds.ShareRegression, VerdictPath = "sweep:r6", Status = "Incomplete" }, rows, Target);
        Assert.Equal((14, 140, 56, 560), share);
        var silence = VerdictCalibrationRadar.CurrentSums(new VerdictCalibrationAlert { Kind = VerdictCalibrationAlertKinds.SilenceShareRegression }, rows, Target);
        Assert.Equal((21, 140, 84, 560), silence);
        var gap = VerdictCalibrationRadar.CurrentSums(new VerdictCalibrationAlert { Kind = VerdictCalibrationAlertKinds.EvidenceGap }, rows, Target);
        Assert.Equal((14, 21, 56, 84), gap);
    }

    [Fact]
    public void Rows_outside_the_horizon_are_ignored()
    {
        var rows = Horizon(w => new[] { ("agent:complete", "Succeeded", 20) });
        rows.Add(new VerdictCalibrationDailyAggregate { TenantId = "t", Date = "2026-08-23", SessionCount = 1000, Buckets = { new VerdictCalibrationBucket { VerdictPath = "sweep:r6", Status = "Incomplete", Count = 1000 } } });
        rows.Add(new VerdictCalibrationDailyAggregate { TenantId = "t", Date = "2026-07-01", SessionCount = 1000, Buckets = { new VerdictCalibrationBucket { VerdictPath = "sweep:r6", Status = "Incomplete", Count = 1000 } } });
        Assert.Empty(VerdictCalibrationRadar.Evaluate(rows, Target));
    }

    // ---- tracker round-trip ----

    [Fact]
    public void Alert_round_trips_through_the_tracker_entity()
    {
        var alert = new VerdictCalibrationAlert
        {
            TenantId = "11111111-1111-1111-1111-111111111111", Kind = VerdictCalibrationAlertKinds.ShareRegression,
            VerdictPath = "rule:ANALYZE-ESP-001", Status = "Failed",
            WindowHitCount = 7, WindowSessionCount = 40, BaselineHitCount = 3, BaselineSessionCount = 160,
            WindowRatePct = 17.5, BaselineRatePct = 1.9, Lift = 9.3, WindowStartDate = "2026-08-16", WindowEndDate = "2026-08-22",
            Dimension = new RuleRegressionDimension { Dimension = "osBuild", Value = "26200.1234", HitCount = 6, HitSharePct = 85.7, AllSharePct = 20.0, Lift = 4.3 },
            FirstNotifiedAt = new DateTime(2026, 8, 20, 6, 0, 0, DateTimeKind.Utc), LastEvaluatedAt = new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc),
        };
        var entity = TableHardwareRejectionNotificationTracker.BuildVerdictCalibrationEntity(alert.TenantId, alert);
        Assert.Equal("verdictcalibration|share_regression|rule:analyze-esp-001|failed", entity.RowKey);
        Assert.Equal(alert.TenantId, entity.PartitionKey);

        var back = TableHardwareRejectionNotificationTracker.MapToVerdictCalibrationAlert(entity);
        Assert.Equal(JsonSerializer.Serialize(alert), JsonSerializer.Serialize(back));

        // Tri-states: no lift / no dimension → absent columns → null on read, never 0 / a guessed dimension.
        var bare = new VerdictCalibrationAlert
        {
            TenantId = alert.TenantId, Kind = VerdictCalibrationAlertKinds.EvidenceGap, VerdictPath = "r6", Status = "*",
            FirstNotifiedAt = alert.FirstNotifiedAt, LastEvaluatedAt = alert.LastEvaluatedAt,
        };
        var bareBack = TableHardwareRejectionNotificationTracker.MapToVerdictCalibrationAlert(
            TableHardwareRejectionNotificationTracker.BuildVerdictCalibrationEntity(bare.TenantId, bare));
        Assert.Null(bareBack.Lift);
        Assert.Null(bareBack.Dimension);
        Assert.Equal("verdictcalibration|evidence_gap|r6|*", TableHardwareRejectionNotificationTracker.BuildVerdictCalibrationRowKey(bare.Kind, bare.VerdictPath, bare.Status));
    }
}
