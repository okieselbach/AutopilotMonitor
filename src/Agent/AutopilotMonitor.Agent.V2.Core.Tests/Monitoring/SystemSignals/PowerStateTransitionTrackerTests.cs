#nullable enable
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Pure state machine behind the power watcher: AC↔battery edges, the 50/30/15 threshold
    /// ladder (latch once per lifetime, lowest-crossed-only on multi-level jumps, baseline
    /// coverage for sessions starting low), null-percent tolerance and the storm cap.
    /// </summary>
    public sealed class PowerStateTransitionTrackerTests
    {
        private static PowerStateResult Ac(int? percent = 80, bool charging = true) => new PowerStateResult
        {
            OnAcPower = true,
            HasBattery = true,
            BatteryPercent = percent,
            IsCharging = charging,
            BatteryLifeMinutes = null,
        };

        private static PowerStateResult Battery(int? percent, int? lifeMinutes = 90) => new PowerStateResult
        {
            OnAcPower = false,
            HasBattery = true,
            BatteryPercent = percent,
            IsCharging = false,
            BatteryLifeMinutes = lifeMinutes,
        };

        [Fact]
        public void Baseline_on_ac_is_silent()
        {
            var tracker = new PowerStateTransitionTracker();
            Assert.Empty(tracker.Baseline(Ac()));
        }

        [Fact]
        public void Baseline_without_battery_or_with_probe_error_is_silent()
        {
            var tracker = new PowerStateTransitionTracker();
            Assert.Empty(tracker.Baseline(new PowerStateResult { OnAcPower = true, HasBattery = false }));
            Assert.Empty(tracker.Baseline(new PowerStateResult { ProbeError = "boom" }));
        }

        [Fact]
        public void Baseline_on_battery_above_ladder_is_silent()
        {
            var tracker = new PowerStateTransitionTracker();
            Assert.Empty(tracker.Baseline(Battery(80)));
        }

        [Fact]
        public void Baseline_at_12_percent_emits_exactly_the_15_threshold()
        {
            var tracker = new PowerStateTransitionTracker();

            var emissions = tracker.Baseline(Battery(12));

            var e = Assert.Single(emissions);
            Assert.Equal(PowerStateTransitionTracker.TransitionThresholdCrossed, e.Transition);
            Assert.Equal(15, e.ThresholdPercent);
            Assert.Equal(12, e.BatteryPercent);
            Assert.Equal(EventSeverity.Error, e.Severity);
            Assert.True(e.ImmediateUpload);
        }

        [Fact]
        public void Baseline_at_25_percent_emits_30_and_latches_50()
        {
            var tracker = new PowerStateTransitionTracker();

            var baseline = tracker.Baseline(Battery(25));
            var e = Assert.Single(baseline);
            Assert.Equal(30, e.ThresholdPercent);
            Assert.Equal(EventSeverity.Warning, e.Severity);
            Assert.True(e.ImmediateUpload);

            // 50 and 30 are latched by the baseline; draining to 40% stays silent, 14% emits 15.
            Assert.Empty(tracker.Evaluate(Battery(40)));
            var next = Assert.Single(tracker.Evaluate(Battery(14)));
            Assert.Equal(15, next.ThresholdPercent);
        }

        [Fact]
        public void Ac_to_battery_emits_warning_with_immediate_upload()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Ac(63));

            var e = Assert.Single(tracker.Evaluate(Battery(63)));

            Assert.Equal(PowerStateTransitionTracker.TransitionAcToBattery, e.Transition);
            Assert.Null(e.ThresholdPercent);
            Assert.Equal(EventSeverity.Warning, e.Severity);
            Assert.True(e.ImmediateUpload);
            Assert.False(e.OnAcPower);
            Assert.Equal(63, e.BatteryPercent);
        }

        [Fact]
        public void Battery_to_ac_emits_info_without_immediate_upload()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Battery(70));

            var e = Assert.Single(tracker.Evaluate(Ac(70)));

            Assert.Equal(PowerStateTransitionTracker.TransitionBatteryToAc, e.Transition);
            Assert.Equal(EventSeverity.Info, e.Severity);
            Assert.False(e.ImmediateUpload);
        }

        [Fact]
        public void No_diff_stays_silent()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Ac());

            Assert.Empty(tracker.Evaluate(Ac()));
            Assert.Empty(tracker.Evaluate(Ac(79)));
        }

        [Fact]
        public void Unplug_below_threshold_emits_transition_and_threshold_together()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Ac(10));

            var emissions = tracker.Evaluate(Battery(10));

            Assert.Equal(2, emissions.Count);
            Assert.Equal(PowerStateTransitionTracker.TransitionAcToBattery, emissions[0].Transition);
            Assert.Equal(PowerStateTransitionTracker.TransitionThresholdCrossed, emissions[1].Transition);
            Assert.Equal(15, emissions[1].ThresholdPercent);
        }

        [Fact]
        public void Multi_level_drop_emits_lowest_crossed_level_only()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Battery(60));

            var e = Assert.Single(tracker.Evaluate(Battery(10)));

            Assert.Equal(15, e.ThresholdPercent);
            // All levels are latched — recovering and re-draining emits nothing more.
            tracker.Evaluate(Ac(90));
            Assert.Single(tracker.Evaluate(Battery(45)), x => x.Transition == PowerStateTransitionTracker.TransitionAcToBattery);
            Assert.Empty(tracker.Evaluate(Battery(13)));
        }

        [Fact]
        public void Each_level_latches_once_per_lifetime()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Battery(70));

            Assert.Single(tracker.Evaluate(Battery(48))); // 50
            Assert.Empty(tracker.Evaluate(Battery(47)));  // still inside 50, no re-emit
            Assert.Single(tracker.Evaluate(Battery(29))); // 30
            Assert.Single(tracker.Evaluate(Battery(15))); // 15 (boundary: <= level)
            Assert.Empty(tracker.Evaluate(Battery(5)));   // ladder exhausted
        }

        [Fact]
        public void Threshold_severity_ladder_is_info_warning_error()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Battery(70));

            var fifty = Assert.Single(tracker.Evaluate(Battery(50)));
            Assert.Equal(EventSeverity.Info, fifty.Severity);
            Assert.False(fifty.ImmediateUpload);

            var thirty = Assert.Single(tracker.Evaluate(Battery(30)));
            Assert.Equal(EventSeverity.Warning, thirty.Severity);
            Assert.True(thirty.ImmediateUpload);

            var fifteen = Assert.Single(tracker.Evaluate(Battery(15)));
            Assert.Equal(EventSeverity.Error, fifteen.Severity);
            Assert.True(fifteen.ImmediateUpload);
        }

        [Fact]
        public void Null_battery_percent_skips_thresholds_but_ac_diff_still_works()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Ac(null));

            var e = Assert.Single(tracker.Evaluate(Battery(null, lifeMinutes: null)));

            Assert.Equal(PowerStateTransitionTracker.TransitionAcToBattery, e.Transition);
            Assert.Null(e.BatteryPercent);
            Assert.Contains("battery level unknown", e.Message);
            Assert.Empty(tracker.Evaluate(Battery(null, lifeMinutes: null)));
        }

        [Fact]
        public void Probe_error_mid_run_preserves_state()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Ac());

            Assert.Empty(tracker.Evaluate(new PowerStateResult { ProbeError = "transient" }));
            // State was not corrupted by the error — the real transition still diffs cleanly.
            Assert.Single(tracker.Evaluate(Battery(60)));
        }

        [Fact]
        public void Evaluate_without_baseline_latches_like_a_baseline()
        {
            var tracker = new PowerStateTransitionTracker();

            var e = Assert.Single(tracker.Evaluate(Battery(12)));

            // No phantom ac_to_battery from the implicit default state — only the threshold.
            Assert.Equal(PowerStateTransitionTracker.TransitionThresholdCrossed, e.Transition);
        }

        [Fact]
        public void Emission_cap_suppresses_and_flips_flag_once()
        {
            var tracker = new PowerStateTransitionTracker(maxEmissions: 4);
            tracker.Baseline(Ac());

            // Flapping power source: each edge is one emission.
            for (var i = 0; i < 2; i++)
            {
                Assert.Single(tracker.Evaluate(Battery(80)));
                Assert.Single(tracker.Evaluate(Ac(80)));
            }
            Assert.False(tracker.EmissionCapReached);

            Assert.Empty(tracker.Evaluate(Battery(80)));
            Assert.True(tracker.EmissionCapReached);
            Assert.Empty(tracker.Evaluate(Ac(80)));
        }

        [Fact]
        public void Message_carries_percent_and_transition_context()
        {
            var tracker = new PowerStateTransitionTracker();
            tracker.Baseline(Ac(62));

            var toBattery = Assert.Single(tracker.Evaluate(Battery(62)));
            Assert.Contains("AC to battery", toBattery.Message);
            Assert.Contains("battery 62%", toBattery.Message);

            var backToAc = Assert.Single(tracker.Evaluate(Ac(63)));
            Assert.Contains("back to AC", backToAc.Message);
            Assert.Contains("charging", backToAc.Message);
        }
    }
}
