using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Replay-safety regression: when <c>ImeLogTracker</c> processes a CMTrace log entry it
    /// stamps <see cref="Monitoring.Enrollment.Ime.ImeLogTracker.LastMatchedLogTimestamp"/>
    /// with the source-line time. The adapter must use that timestamp on both the emitted
    /// <see cref="DecisionSignal"/> and the corresponding informational event — collapsing
    /// to <c>_clock.UtcNow</c> would lose forensic time on agent first-boot replay.
    /// <para>
    /// Pathological-clock guard: a source timestamp older than 24h or in the future by &gt;1h
    /// is rejected; the adapter falls back to the clock and tags the event with
    /// <c>derivedTimestamp=true</c> + <c>rejectedSourceTimestamp=&lt;original&gt;</c> so the
    /// anomaly is forensically visible (per <c>feedback_timestamp_clamping</c>).
    /// </para>
    /// </summary>
    public sealed class ImeLogTrackerAdapterSourceTimestampTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void EspPhase_uses_source_log_timestamp_when_present()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var sourceTs = ClockNow.AddMinutes(-30);
            f.Tracker.LastMatchedLogTimestamp = sourceTs;

            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            // DecisionSignal carries the source time, NOT clock.UtcNow.
            var decision = f.DecisionSignals(DecisionSignalKind.EspPhaseChanged).Single();
            Assert.Equal(sourceTs, decision.OccurredAtUtc);
            Assert.False(decision.Evidence!.DerivationInputs!.ContainsKey("derivedTimestamp"));

            // InformationalEvent mirrors the source time so the timeline ordering on the UI
            // shows when the phase actually transitioned, not when the agent caught up.
            var info = f.InfoEvent(SharedEventTypes.EspPhaseChanged);
            Assert.Equal(sourceTs, info.OccurredAtUtc);
            Assert.False(info.Payload!.ContainsKey("derivedTimestamp"));
        }

        [Fact]
        public void EspPhase_falls_back_to_clock_when_no_source_timestamp_and_flags_derivation()
        {
            // No LastMatchedLogTimestamp set — fixture default is null.
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            var decision = f.DecisionSignals(DecisionSignalKind.EspPhaseChanged).Single();
            Assert.Equal(ClockNow, decision.OccurredAtUtc);
            Assert.Equal("true", decision.Evidence!.DerivationInputs!["derivedTimestamp"]);

            var info = f.InfoEvent(SharedEventTypes.EspPhaseChanged);
            Assert.Equal(ClockNow, info.OccurredAtUtc);
            Assert.Equal("true", info.Payload!["derivedTimestamp"]);
        }

        [Fact]
        public void EspPhase_clamps_pathologically_old_source_timestamp_and_flags_rejected_value()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // 25h-old source timestamp — pathological (skewed CMTrace clock or multi-day log).
            var ancient = ClockNow.AddHours(-25);
            f.Tracker.LastMatchedLogTimestamp = ancient;

            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            var decision = f.DecisionSignals(DecisionSignalKind.EspPhaseChanged).Single();
            Assert.Equal(ClockNow, decision.OccurredAtUtc);
            Assert.Equal("true", decision.Evidence!.DerivationInputs!["derivedTimestamp"]);
            Assert.Equal(ancient.ToString("o"), decision.Evidence.DerivationInputs["rejectedSourceTimestamp"]);

            var info = f.InfoEvent(SharedEventTypes.EspPhaseChanged);
            Assert.Equal("true", info.Payload!["derivedTimestamp"]);
            Assert.Equal(ancient.ToString("o"), info.Payload["rejectedSourceTimestamp"]);
        }

        [Fact]
        public void UserSessionCompleted_uses_source_log_timestamp_when_present()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var sourceTs = ClockNow.AddMinutes(-15);
            f.Tracker.LastMatchedLogTimestamp = sourceTs;

            adapter.TriggerUserSessionCompletedFromTest();

            var decision = f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted).Single();
            Assert.Equal(sourceTs, decision.OccurredAtUtc);

            var info = f.InfoEvent(SharedEventTypes.ImeUserSessionCompleted);
            Assert.Equal(sourceTs, info.OccurredAtUtc);
            Assert.Equal(sourceTs.ToString("o"), info.Payload!["detectedAt"]);
        }

        [Fact]
        public void AppStateChange_uses_source_log_timestamp_when_present()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var sourceTs = ClockNow.AddMinutes(-5);
            f.Tracker.LastMatchedLogTimestamp = sourceTs;
            var app = new AppPackageState("app-X", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Installed);

            var decision = f.DecisionSignals(DecisionSignalKind.AppInstallCompleted).Single();
            Assert.Equal(sourceTs, decision.OccurredAtUtc);

            var info = f.InfoEvent(SharedEventTypes.AppInstallComplete);
            Assert.Equal(sourceTs, info.OccurredAtUtc);
            // Timing payload also pinned to source time — startedAt/completedAt reflect when
            // the app *actually* finished, not when the agent read the log line.
            Assert.Equal(sourceTs.ToString("o"), info.Payload!["completedAt"]);
        }

        [Fact]
        public void ScriptCompleted_fallback_binds_to_exit_timestamp_and_drops_stale_patternId()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // The exit-code fallback emits up to a grace period after the script's exit line, by
            // which point an UNRELATED later line is the "last matched" one. The fallback event must
            // bind to the script's own exit timestamp and must NOT inherit the stale patternId.
            var exitAt = ClockNow.AddMinutes(-3);
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddMinutes(-1); // unrelated later line
            f.Tracker.LastMatchedPatternId = "UNRELATED-LATER-PATTERN";

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "dece354a",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
                ResultSource = "agentexecutor_fallback",
                ExitObservedAtUtc = exitAt,
            });

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal(exitAt, info.OccurredAtUtc);
            Assert.False(info.Payload!.ContainsKey("patternId"));
            Assert.Equal("agentexecutor_fallback", info.Payload["resultSource"]);
        }

        [Fact]
        public void ScriptCompleted_authoritative_path_keeps_patternId_and_last_matched_timestamp()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // Regression guard: the normal IME-result path is unchanged — it still uses the
            // last-matched timestamp + patternId (the PS-SCRIPT-RESULT line that drove the emit).
            var sourceTs = ClockNow.AddMinutes(-2);
            f.Tracker.LastMatchedLogTimestamp = sourceTs;
            f.Tracker.LastMatchedPatternId = "PS-SCRIPT-RESULT";

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "35ed39d9",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
                ResultSource = "ime_policy_result",
                ExitObservedAtUtc = ClockNow.AddMinutes(-30), // present but must be ignored on this path
            });

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal(sourceTs, info.OccurredAtUtc);
            Assert.Equal("PS-SCRIPT-RESULT", info.Payload!["patternId"]);
            Assert.Equal("ime_policy_result", info.Payload["resultSource"]);
        }

        [Fact]
        public void WhiteGloveSealingPattern_uses_source_log_timestamp()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(
                f.Tracker, f.Ingress, f.Clock,
                whiteGloveSealingPatternIds: new[] { "wg-seal-pattern" });

            var sourceTs = ClockNow.AddMinutes(-2);
            f.Tracker.LastMatchedLogTimestamp = sourceTs;

            adapter.TriggerPatternMatchedFromTest("wg-seal-pattern");

            var decision = f.DecisionSignals(DecisionSignalKind.WhiteGloveSealingPatternDetected).Single();
            Assert.Equal(sourceTs, decision.OccurredAtUtc);
        }
    }
}
