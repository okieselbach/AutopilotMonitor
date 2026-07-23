using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Historic-replay suppression for APP events (session eaf3d8c4 part 2): 147 of 157 app
    /// events in that session were replayed from a previous enrollment's IME log — all 10
    /// "installed apps" were phantoms. Stale app-state transitions and DO telemetry must be
    /// suppressed BEFORE the sub-phase declaration, the timing bookkeeping and the terminal
    /// DecisionSignal dedup, so fire-once flags stay unconsumed and no phantom
    /// AppInstallCompleted/Failed reaches the engine (which could spuriously re-arm the
    /// AdvisoryCompletion window). The script twin lives in
    /// <see cref="ImeLogTrackerAdapterHistoricReplayTests"/>.
    /// </summary>
    public sealed class ImeLogTrackerAdapterHistoricAppReplayTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 7, 23, 15, 42, 0, DateTimeKind.Utc);

        [Fact]
        public void Stale_terminal_app_transition_is_fully_suppressed()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-old", 0);

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddDays(-7);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);

            Assert.Empty(f.InfoEvents(SharedEventTypes.AppInstallComplete));
            Assert.Empty(f.DecisionSignals(DecisionSignalKind.AppInstallCompleted));
            // Shadow download_progress (status=completed) and the summary snapshot ride the
            // same emit — both must be gone too.
            Assert.Empty(f.InfoEvents(SharedEventTypes.DownloadProgress));
            Assert.Empty(f.InfoEvents(SharedEventTypes.AppTrackingSummary));
            // Timing bookkeeping must not be poisoned (feeds FinalStatusBuilder).
            Assert.False(adapter.AppTimings.ContainsKey("app-old"));

            Assert.Single(f.InfoEvents(SharedEventTypes.HistoricImeReplayDetected));
        }

        [Fact]
        public void Fresh_run_after_suppressed_stale_run_fires_signal_timing_and_subphase()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-X", 0);

            // Fresh ESP phase so the sub-phase declaration is armed.
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddMinutes(-5);
            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            // Stale replay of the same app — suppressed, must consume NEITHER the sub-phase
            // fire-once NOR the terminal-signal dedup for this appId.
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddDays(-7);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Installed);
            Assert.Empty(f.InfoEvents(SharedEventTypes.PhaseTransition));
            Assert.Empty(f.DecisionSignals(DecisionSignalKind.AppInstallCompleted));

            // The app genuinely runs in THIS enrollment.
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddMinutes(-2);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Installing);
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddMinutes(-1);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);

            Assert.Single(f.InfoEvents(SharedEventTypes.PhaseTransition));            // sub-phase fired fresh
            Assert.Single(f.DecisionSignals(DecisionSignalKind.AppInstallCompleted)); // signal fired fresh
            var timing = adapter.AppTimings["app-X"];
            Assert.Equal(ClockNow.AddMinutes(-2), timing.StartedAtUtc);               // fresh stamps, not stale
            Assert.Equal(ClockNow.AddMinutes(-1), timing.CompletedAtUtc);
        }

        [Fact]
        public void Stale_do_telemetry_is_suppressed_fresh_emits()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-do", 0);

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddDays(-7);
            adapter.TriggerDoTelemetryFromTest(app);
            Assert.Empty(f.InfoEvents(SharedEventTypes.DoTelemetry));

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddMinutes(-1);
            adapter.TriggerDoTelemetryFromTest(app);
            Assert.Single(f.InfoEvents(SharedEventTypes.DoTelemetry));
        }

        [Fact]
        public void Oneshot_summary_is_shared_between_script_and_app_suppressions()
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
            adapter.TriggerAppStateFromTest(new AppPackageState("app-old", 0),
                AppInstallationState.Installing, AppInstallationState.Installed);

            Assert.Single(f.InfoEvents(SharedEventTypes.HistoricImeReplayDetected));
        }

        [Fact]
        public void FutureSkew_app_transition_still_emits()
        {
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-skew", 0);

            // Mid-enrollment clock jump — genuine current activity, stamped to the clock.
            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddHours(2);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.AppInstallComplete));
            Assert.Equal(ClockNow, info.OccurredAtUtc);
            Assert.Equal("true", info.Payload!["derivedTimestamp"]);
            Assert.Single(f.DecisionSignals(DecisionSignalKind.AppInstallCompleted));
            Assert.Empty(f.InfoEvents(SharedEventTypes.HistoricImeReplayDetected));
        }

        [Fact]
        public void SimulationMode_bypasses_suppression_for_apps_and_scripts()
        {
            // The --replay-log-dir dev tool intentionally feeds historic lines.
            using var f = new ImeLogTrackerAdapterFixture(ClockNow);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            f.Tracker.SimulationMode = true;

            f.Tracker.LastMatchedLogTimestamp = ClockNow.AddDays(-7);
            adapter.TriggerAppStateFromTest(new AppPackageState("app-sim", 0),
                AppInstallationState.Installing, AppInstallationState.Installed);
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "bbb22222",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
            });

            Assert.Single(f.InfoEvents(SharedEventTypes.AppInstallComplete));
            Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Empty(f.InfoEvents(SharedEventTypes.HistoricImeReplayDetected));
        }
    }
}
