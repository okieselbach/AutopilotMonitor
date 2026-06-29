using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Plan §5 Fix 4c — adapter tracks per-app install-lifecycle timing and surfaces it on
    /// <c>app_install_*</c> event DataJson (<c>startedAt</c> / <c>completedAt</c> /
    /// <c>durationSeconds</c>) + via the new <see cref="ImeLogTrackerAdapter.AppTimings"/>
    /// snapshot for downstream consumers (FinalStatusBuilder, app_tracking_summary).
    /// </summary>
    public sealed class ImeLogTrackerAdapterTimingTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void First_Installing_transition_stamps_StartedAt_on_adapter_and_payload()
        {
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-A", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Installing);

            var info = f.InfoEvent(SharedEventTypes.AppInstallStart);
            Assert.Equal(T0.ToString("o"), info.Payload!["startedAt"]);
            Assert.False(info.Payload.ContainsKey("completedAt"));
            Assert.False(info.Payload.ContainsKey("durationSeconds"));

            Assert.True(adapter.AppTimings.ContainsKey("app-A"));
            Assert.Equal(T0, adapter.AppTimings["app-A"].StartedAtUtc);
            Assert.Null(adapter.AppTimings["app-A"].CompletedAtUtc);
        }

        [Fact]
        public void Downloading_transition_also_stamps_StartedAt()
        {
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-D", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Downloading);

            Assert.Equal(T0, adapter.AppTimings["app-D"].StartedAtUtc);
            var info = f.InfoEvent(SharedEventTypes.AppDownloadStarted);
            Assert.Equal(T0.ToString("o"), info.Payload!["startedAt"]);
        }

        [Fact]
        public void Terminal_transition_stamps_CompletedAt_and_emits_DurationSeconds()
        {
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-T", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Installing);
            f.Clock.Advance(TimeSpan.FromSeconds(42.5));
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);

            var completeEvent = f.InfoEvent(SharedEventTypes.AppInstallComplete);
            Assert.Equal(T0.ToString("o"), completeEvent.Payload!["startedAt"]);
            Assert.Equal(T0.AddSeconds(42.5).ToString("o"), completeEvent.Payload["completedAt"]);
            Assert.Equal("42.50", completeEvent.Payload["durationSeconds"]);

            var timing = adapter.AppTimings["app-T"];
            Assert.Equal(T0, timing.StartedAtUtc);
            Assert.Equal(T0.AddSeconds(42.5), timing.CompletedAtUtc);
            Assert.Equal(42.5, timing.DurationSeconds);
        }

        [Fact]
        public void Error_transition_also_sets_CompletedAt()
        {
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-E", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Installing);
            f.Clock.Advance(TimeSpan.FromSeconds(3));
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Error);

            var failedEvent = f.InfoEvent(SharedEventTypes.AppInstallFailed);
            Assert.Equal("3.00", failedEvent.Payload!["durationSeconds"]);
            Assert.NotNull(adapter.AppTimings["app-E"].CompletedAtUtc);
        }

        [Fact]
        public void Timing_is_set_once_not_overwritten_by_subsequent_same_state_events()
        {
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-Z", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Installing);
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installing);
            f.Clock.Advance(TimeSpan.FromSeconds(5));
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);

            var timing = adapter.AppTimings["app-Z"];
            // StartedAt = first Installing (T0), NOT the second one.
            Assert.Equal(T0, timing.StartedAtUtc);
            // CompletedAt = Installed stamp (T0 + 15s).
            Assert.Equal(T0.AddSeconds(15), timing.CompletedAtUtc);
        }

        [Fact]
        public void AppTimings_snapshot_is_an_independent_copy()
        {
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0),
                AppInstallationState.Unknown, AppInstallationState.Installing);

            var snap1 = adapter.AppTimings;

            f.Clock.Advance(TimeSpan.FromSeconds(5));
            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0),
                AppInstallationState.Installing, AppInstallationState.Installed);

            // snap1 captured before terminal — CompletedAt must still be null.
            Assert.Null(snap1["a"].CompletedAtUtc);
            // Fresh snap reflects the terminal stamp.
            Assert.NotNull(adapter.AppTimings["a"].CompletedAtUtc);
        }

        // Remediation (health) scripts: the cycle start is captured from HS-SCRIPT-START and the
        // whole-cycle run duration surfaces on the HS-NEW-RESULT phase events — the analog of the
        // platform-script start→completion timing.
        private const string DetectOnlyResultJson = @"{
          ""PolicyId"": ""75d14a95-d49f-473d-9d65-d4b006bc7468"",
          ""PreRemediationDetectScriptOutput"": ""LocalAdminIsEnabled=False"",
          ""RemediationStatus"": 4,
          ""ErrorCode"": 0,
          ""Info"": { ""FirstDetectExitCode"": 0, ""ErrorDetails"": null },
          ""RunAsAccount"": 1,
          ""TargetType"": 2
        }";

        [Fact]
        public void HealthScriptResult_with_observed_start_emits_cycle_durationSeconds()
        {
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // Cycle started 65s before the consolidated result line is parsed (clock stays at T0
            // because no regex match drives LastMatchedLogTimestamp in this seam).
            f.Tracker.SeedHealthScriptStartForTesting(
                "75d14a95-d49f-473d-9d65-d4b006bc7468", T0.AddSeconds(-65));

            f.Tracker.HandleHealthScriptResultJson(DetectOnlyResultJson);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("65.00", info.Payload!["durationSeconds"]);
        }

        [Fact]
        public void HealthScriptResult_without_observed_start_omits_durationSeconds()
        {
            // Replay scenario: the result line is seen but its HS-SCRIPT-START scrolled past
            // before the agent booted, so no start timestamp is available → no duration.
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.HandleHealthScriptResultJson(DetectOnlyResultJson);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.False(info.Payload!.ContainsKey("durationSeconds"));
        }

        [Fact]
        public void DownloadProgress_tick_does_not_regress_StartedAt()
        {
            // Fix 1 policy: download_progress is ImmediateUpload=true. Fix 4c must not let a
            // mid-download progress tick rewrite the earlier Downloading-start stamp.
            using var f = new ImeLogTrackerAdapterFixture(T0);
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-P", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Downloading);
            var firstStamp = adapter.AppTimings["app-P"].StartedAtUtc;

            f.Clock.Advance(TimeSpan.FromSeconds(9));
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Downloading);

            Assert.Equal(firstStamp, adapter.AppTimings["app-P"].StartedAtUtc);
        }
    }
}
