using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Historic-replay handling (session eaf3d8c4): a previous enrollment's IME log surviving
    /// on disk made the agent replay week-old script activity — 156 phantom script_completed
    /// events with ~170 h durations (clock-clamped completion minus RAW stale start) and an
    /// immediate-upload flood. Script events whose source line is &gt; 24 h stale are now
    /// suppressed entirely (one-shot <c>historic_script_replay_detected</c> summary instead),
    /// and durations are computed from a timeline-consistent timestamp pair with a 24 h
    /// plausibility backstop.
    /// </summary>
    public sealed class ImeLogTrackerAdapterHistoricReplayTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 7, 23, 15, 42, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------------
        // Suppression: stale (> 24 h past) source lines are a previous enrollment
        // ---------------------------------------------------------------------

        [Fact]
        public void Stale_completion_is_suppressed_and_emits_oneshot_summary()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var ancient = ClockNow.AddDays(-7);
            f.Tracker.LastMatchedLogTimestamp = ancient;

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "d78c1822",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Failed",
                StartedAtUtc = ancient.AddMinutes(-30),
                // Bootstrap marker in stdout — week-old evidence must not drive the
                // bootstrap-detected trace either.
                Stdout = "Bootstrap script version: v2.0",
            });

            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptFailed));
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
            Assert.Empty(f.InfoEvents(SharedEventTypes.AgentTrace));

            var summary = f.InfoEvent(SharedEventTypes.HistoricScriptReplayDetected);
            Assert.Equal(ancient.ToString("o"), summary.Payload!["earliestRejectedSourceTimestamp"]);
        }

        [Fact]
        public void Summary_is_oneshot_across_started_and_completed_suppressions()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddDays(-7);
            adapter.TriggerScriptStartedFromTest(new ScriptStartedInfo { PolicyId = "aaa11111", ScriptType = "platform" });

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddDays(-7).AddMinutes(1);
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "aaa11111",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
            });

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddDays(-7).AddMinutes(2);
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "bbb22222",
                ScriptType = "remediation",
                ScriptPart = "detection",
                ExitCode = 0,
                ComplianceResult = "True",
            });

            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptStarted));
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            // Exactly ONE summary regardless of how many replayed lines followed — and it dates
            // the replay window from the FIRST (earliest) suppressed line.
            var summary = Assert.Single(f.InfoEvents(SharedEventTypes.HistoricScriptReplayDetected));
            Assert.Equal(ClockNow.AddDays(-7).ToString("o"), summary.Payload!["earliestRejectedSourceTimestamp"]);
        }

        [Fact]
        public void Fresh_events_after_suppression_emit_normally()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddDays(-7);
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "aaa11111",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
            });
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptCompleted));

            // The agent catches up to current log content — normal emission resumes.
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddMinutes(-2);
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "ccc33333",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
                StartedAtUtc = ClockNow.AddMinutes(-3),
            });

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("ccc33333", info.Payload!["policyId"]);
            Assert.Equal("60.00", info.Payload["durationSeconds"]);
        }

        [Fact]
        public void Clock_fallback_without_source_timestamp_still_emits()
        {
            // derivedFromClock with NO rejected source ts (synthetic/callback path) is not a
            // replay — must emit, and must not trigger the summary.
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "ddd44444",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
            });

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("true", info.Payload!["derivedTimestamp"]);
            Assert.False(info.Payload.ContainsKey("rejectedSourceTimestamp"));
            Assert.Empty(f.InfoEvents(SharedEventTypes.HistoricScriptReplayDetected));
        }

        // ---------------------------------------------------------------------
        // Duration: timeline-consistent pair + plausibility backstop
        // ---------------------------------------------------------------------

        [Fact]
        public void FutureSkew_completion_emits_with_clamped_stamp_and_raw_pair_duration()
        {
            // Mid-enrollment clock jump (WhiteGlove +1h CMTrace skew case): the source ts is
            // rejected as future-skewed — NOT suppressed — and the duration must come from the
            // raw source pair (both ends on the same skewed timeline), not clamped-now − raw-start.
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var skewed = ClockNow.AddHours(2);
            f.Tracker.LastMatchedLogTimestamp = skewed;

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "eee55555",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
                StartedAtUtc = skewed.AddSeconds(-360),
            });

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal(ClockNow, info.OccurredAtUtc);                       // stamp: clamped to clock
            Assert.Equal("true", info.Payload!["derivedTimestamp"]);
            Assert.Equal(skewed.ToString("o"), info.Payload["rejectedSourceTimestamp"]);
            Assert.Equal("360.00", info.Payload["durationSeconds"]);          // duration: raw pair
            Assert.Empty(f.InfoEvents(SharedEventTypes.HistoricScriptReplayDetected));
        }

        [Fact]
        public void FutureSkew_failed_platform_script_timeout_heuristic_uses_raw_pair()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var skewed = ClockNow.AddHours(2);
            f.Tracker.LastMatchedLogTimestamp = skewed;

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "fff66666",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Failed",
                StartedAtUtc = skewed.AddMinutes(-26),  // past the 25-min suspicion threshold
            });

            var suspected = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
            Assert.Equal("1560.00", suspected.Payload!["durationSeconds"]);
            Assert.Equal(ClockNow, suspected.OccurredAtUtc);
        }

        [Fact]
        public void Implausible_duration_above_24h_is_omitted()
        {
            // Fresh (accepted) completion paired with an ancient start — e.g. a stale pending
            // slot a legacy path left behind. The duration would be a cross-run lie; omit it.
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddMinutes(-2);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "abc12345",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Failed",
                StartedAtUtc = ClockNow.AddHours(-30),
            });

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptFailed));
            Assert.False(info.Payload!.ContainsKey("durationSeconds"));
            Assert.False(info.Payload.ContainsKey("durationBasis"));
            // The 30 h "duration" must not read as a hung script either.
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptTimeoutSuspected));
        }

        // ---------------------------------------------------------------------
        // Tracker slot hardening: stale pending start must not pair with a fresh run
        // ---------------------------------------------------------------------

        [Fact]
        public void Fresh_start_discards_stale_pending_slot_from_previous_enrollment()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // Previous enrollment: replayed start line whose matching result never appeared
            // (the script was mid-run when that enrollment ended).
            f.Tracker.SeedPendingPlatformScriptForTesting("beef7777", exitCode: null, exitObservedAtUtc: null,
                startedAtUtc: ClockNow.AddDays(-7));

            // Same policy genuinely re-runs now — the fresh start must begin a NEW execution.
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddSeconds(-90);
            f.Tracker.HandlePlatformScriptStarted("beef7777");

            f.Tracker.LastMatchedLogTimestamp = ClockNow;
            f.Tracker.CompletePlatformScriptFromImeResultForTesting("beef7777", "Success", ClockNow);

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            // Without the hardening the slot kept the 7-day-old start: the duration would have
            // been dropped by the 24 h backstop (or, before that, shown as ~168 h).
            Assert.Equal("90.00", info.Payload!["durationSeconds"]);
        }
    }
}
