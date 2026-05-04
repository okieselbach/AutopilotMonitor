#nullable enable
using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Termination;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Termination
{
    public sealed class FinalStatusBuilderTests
    {
        private static DateTime StartUtc => new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
        private static DateTime EndUtc => StartUtc.AddMinutes(45);

        private static DecisionState StateWith(SessionStage stage, string? helloOutcome = null)
        {
            var initial = DecisionState.CreateInitial("S1", "T1");
            var builder = new DecisionStateBuilder(initial) { Stage = stage };
            if (helloOutcome != null)
                builder.HelloOutcome = new SignalFact<string>(helloOutcome, sourceSignalOrdinal: 1);
            return builder.Build();
        }

        private static EnrollmentTerminatedEventArgs Args(
            EnrollmentTerminationReason reason,
            EnrollmentTerminationOutcome outcome,
            SessionStage stage) =>
            new EnrollmentTerminatedEventArgs(
                reason: reason,
                outcome: outcome,
                stageName: stage.ToString(),
                terminatedAtUtc: EndUtc);

        [Theory]
        [InlineData(SessionStage.Completed, EnrollmentTerminationOutcome.Succeeded, "succeeded")]
        [InlineData(SessionStage.Failed, EnrollmentTerminationOutcome.Failed, "failed")]
        [InlineData(SessionStage.WhiteGloveSealed, EnrollmentTerminationOutcome.Succeeded, "whiteglove_part1")]
        public void Build_maps_outcome_from_terminal_stage(SessionStage stage, EnrollmentTerminationOutcome outcome, string expected)
        {
            var state = StateWith(stage);
            var args = Args(EnrollmentTerminationReason.DecisionTerminalStage, outcome, stage);

            var status = FinalStatusBuilder.Build(state, args, packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.Equal(expected, status.Outcome);
        }

        [Fact]
        public void Build_reports_agent_uptime_in_seconds()
        {
            var state = StateWith(SessionStage.Completed);
            var args = Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed);

            var status = FinalStatusBuilder.Build(state, args, packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.Equal((EndUtc - StartUtc).TotalSeconds, status.AgentUptimeSeconds);
        }

        [Fact]
        public void Build_records_completion_source_from_reason()
        {
            var state = StateWith(SessionStage.Completed);
            var args = Args(EnrollmentTerminationReason.MaxLifetimeExceeded, EnrollmentTerminationOutcome.TimedOut, SessionStage.Completed);

            var status = FinalStatusBuilder.Build(state, args, packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.Equal(nameof(EnrollmentTerminationReason.MaxLifetimeExceeded), status.CompletionSource);
        }

        [Fact]
        public void Build_reports_hello_outcome_from_fact_or_unknown()
        {
            var state1 = StateWith(SessionStage.Completed, helloOutcome: "completed");
            var status1 = FinalStatusBuilder.Build(state1,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: null, agentStartTimeUtc: StartUtc);
            Assert.Equal("completed", status1.HelloOutcome);

            var state2 = StateWith(SessionStage.Completed, helloOutcome: null);
            var status2 = FinalStatusBuilder.Build(state2,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: null, agentStartTimeUtc: StartUtc);
            Assert.Equal("unknown", status2.HelloOutcome);
        }

        [Fact]
        public void Build_signalsSeen_from_decision_state_facts()
        {
            var initial = DecisionState.CreateInitial("S1", "T1");
            var state = new DecisionStateBuilder(initial)
            {
                Stage = SessionStage.Completed,
                HelloResolvedUtc = new SignalFact<DateTime>(StartUtc.AddMinutes(5), 1),
                DesktopArrivedUtc = new SignalFact<DateTime>(StartUtc.AddMinutes(10), 2),
                ScenarioObservations = EnrollmentScenarioObservations.Empty.WithAadUserJoinWithUserObserved(value: true, sourceSignalOrdinal: 3),
                ImeMatchedPatternId = new SignalFact<string>("esp_done", 4),
            }.Build();

            var status = FinalStatusBuilder.Build(state,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.Contains("hello_resolved", status.SignalsSeen);
            Assert.Contains("desktop_arrived", status.SignalsSeen);
            Assert.Contains("aad_user_joined", status.SignalsSeen);
            Assert.Contains("ime_pattern:esp_done", status.SignalsSeen);
        }

        [Fact]
        public void Build_with_null_package_states_produces_empty_app_summary()
        {
            var state = StateWith(SessionStage.Completed);
            var status = FinalStatusBuilder.Build(state,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.Equal(0, status.AppSummary.TotalApps);
            Assert.Empty(status.AppSummary.AppsByPhase);
            Assert.Empty(status.PackageStatesByPhase);
        }

        [Fact]
        public void Build_counts_apps_by_installation_state_and_targeted_phase()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var list = new AppPackageStateList(logger);

            var okDevice = new AppPackageState("app-1", 0); // default installationState = Unknown
            okDevice.UpdateState(AppInstallationState.Installed);
            TestHelpers.SetTargeted(okDevice, AppTargeted.Device);
            list.Add(okDevice);

            var errDevice = new AppPackageState("app-2", 1);
            errDevice.UpdateState(AppInstallationState.Error);
            TestHelpers.SetTargeted(errDevice, AppTargeted.Device);
            list.Add(errDevice);

            var errUser = new AppPackageState("app-3", 2);
            errUser.UpdateState(AppInstallationState.Error);
            TestHelpers.SetTargeted(errUser, AppTargeted.User);
            list.Add(errUser);

            var inProgress = new AppPackageState("app-4", 3);
            inProgress.UpdateState(AppInstallationState.InProgress);
            TestHelpers.SetTargeted(inProgress, AppTargeted.Device);
            list.Add(inProgress);

            var state = StateWith(SessionStage.Completed);
            var status = FinalStatusBuilder.Build(state,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: list, agentStartTimeUtc: StartUtc);

            Assert.Equal(4, status.AppSummary.TotalApps);
            Assert.Equal(3, status.AppSummary.CompletedApps); // Installed + 2 Errors count as completed (terminal)
            Assert.Equal(2, status.AppSummary.ErrorCount);
            Assert.Equal(1, status.AppSummary.DeviceErrors);
            Assert.Equal(1, status.AppSummary.UserErrors);
            Assert.Equal(3, status.AppSummary.AppsByPhase["Device"]);
            Assert.Equal(1, status.AppSummary.AppsByPhase["User"]);

            Assert.True(status.PackageStatesByPhase.ContainsKey("Device"));
            Assert.True(status.PackageStatesByPhase.ContainsKey("User"));
            Assert.Equal(3, status.PackageStatesByPhase["Device"].Count);
            Assert.Single(status.PackageStatesByPhase["User"]);
        }

        [Fact]
        public void Build_includes_per_app_timing_when_app_timings_provided()
        {
            // Plan §5 Fix 4a — FinalStatusBuilder surfaces StartedAt / CompletedAt /
            // DurationSeconds on per-app package rows when the adapter's timing snapshot is
            // available. Null snapshot → timing fields stay null (dialog distinguishes the two).
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var list = new AppPackageStateList(logger);
            var installed = new AppPackageState("app-timed", 0);
            installed.UpdateState(AppInstallationState.Installed);
            TestHelpers.SetTargeted(installed, AppTargeted.Device);
            list.Add(installed);
            var untimed = new AppPackageState("app-untimed", 1);
            untimed.UpdateState(AppInstallationState.Installed);
            TestHelpers.SetTargeted(untimed, AppTargeted.Device);
            list.Add(untimed);

            var timings = new System.Collections.Generic.Dictionary<string, AppInstallTiming>
            {
                ["app-timed"] = new AppInstallTiming(StartUtc.AddMinutes(2), StartUtc.AddMinutes(3)),
                // app-untimed intentionally absent — builder should omit fields
            };

            var status = FinalStatusBuilder.Build(
                StateWith(SessionStage.Completed),
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: list,
                agentStartTimeUtc: StartUtc,
                appTimings: timings);

            var timedInfo = status.PackageStatesByPhase["Device"].Single(p => p.AppName == "app-timed");
            Assert.Equal(StartUtc.AddMinutes(2).ToString("o"), timedInfo.StartedAt);
            Assert.Equal(StartUtc.AddMinutes(3).ToString("o"), timedInfo.CompletedAt);
            Assert.Equal(60.0, timedInfo.DurationSeconds);

            var untimedInfo = status.PackageStatesByPhase["Device"].Single(p => p.AppName == "app-untimed");
            Assert.Null(untimedInfo.StartedAt);
            Assert.Null(untimedInfo.CompletedAt);
            Assert.Null(untimedInfo.DurationSeconds);
        }

        [Fact]
        public void Build_with_null_app_timings_still_produces_package_states()
        {
            // Backward-compat: when appTimings is not passed (older call sites / tests), the
            // builder uses an empty timing map and the timing fields stay null on every row.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var list = new AppPackageStateList(logger);
            var app = new AppPackageState("a", 0);
            app.UpdateState(AppInstallationState.Installed);
            TestHelpers.SetTargeted(app, AppTargeted.Device);
            list.Add(app);

            var status = FinalStatusBuilder.Build(
                StateWith(SessionStage.Completed),
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: list,
                agentStartTimeUtc: StartUtc,
                appTimings: null);

            var info = status.PackageStatesByPhase["Device"].Single();
            Assert.Null(info.StartedAt);
            Assert.Null(info.CompletedAt);
            Assert.Null(info.DurationSeconds);
        }

        [Fact]
        public void Build_serializes_to_json_with_expected_property_names()
        {
            var state = StateWith(SessionStage.Completed);
            var status = FinalStatusBuilder.Build(state,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: null, agentStartTimeUtc: StartUtc);

            var json = JsonConvert.SerializeObject(status);

            // Dialog-wire contract: all fields lower-camelCase (matches SummaryDialog's FinalStatus model).
            Assert.Contains("\"schemaVersion\":2", json);
            Assert.Contains("\"timestamp\":", json);
            Assert.Contains("\"outcome\":", json);
            Assert.Contains("\"completionSource\":", json);
            Assert.Contains("\"helloOutcome\":", json);
            Assert.Contains("\"enrollmentType\":", json);
            Assert.Contains("\"agentUptimeSeconds\":", json);
            Assert.Contains("\"signalsSeen\":", json);
            Assert.Contains("\"appSummary\":", json);
            Assert.Contains("\"packageStatesByPhase\":", json);
        }

        // ============================================================ Schema 2 fields

        [Fact]
        public void Build_emits_schema_version_two()
        {
            // Sentinel that lets the dialog branch into the V2 renderer. Locked at 2 until
            // a deliberate schema bump.
            var status = FinalStatusBuilder.Build(StateWith(SessionStage.Completed),
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: null, agentStartTimeUtc: StartUtc);
            Assert.Equal(2, status.SchemaVersion);
        }

        [Theory]
        [InlineData(SessionStage.Completed, EnrollmentTerminationOutcome.Succeeded, "succeeded")]
        [InlineData(SessionStage.Failed, EnrollmentTerminationOutcome.Failed, "failed")]
        [InlineData(SessionStage.WhiteGloveSealed, EnrollmentTerminationOutcome.Succeeded, "whiteglove_part1")]
        public void Build_outcome_carries_v2_granularity(SessionStage stage, EnrollmentTerminationOutcome outcome, string expected)
        {
            // Schema 2 keeps the V2-enum granularity in the wire format. The dialog reads
            // schemaVersion to know it can interpret these new strings.
            var status = FinalStatusBuilder.Build(StateWith(stage),
                Args(EnrollmentTerminationReason.DecisionTerminalStage, outcome, stage),
                packageStates: null, agentStartTimeUtc: StartUtc);
            Assert.Equal(expected, status.Outcome);
        }

        [Fact]
        public void Build_failure_reason_uses_terminated_details_first()
        {
            // When the orchestrator supplies a specific Details message we use it verbatim —
            // it's the most actionable string available to the user.
            var args = new EnrollmentTerminatedEventArgs(
                EnrollmentTerminationReason.DecisionTerminalStage,
                EnrollmentTerminationOutcome.Failed,
                SessionStage.Failed.ToString(),
                EndUtc,
                details: "ESP failed: 0x800705B4 (provisioning timeout).");

            var status = FinalStatusBuilder.Build(StateWith(SessionStage.Failed), args,
                packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.Equal("ESP failed: 0x800705B4 (provisioning timeout).", status.FailureReason);
        }

        [Fact]
        public void Build_failure_reason_for_max_lifetime_timeout()
        {
            var args = Args(EnrollmentTerminationReason.MaxLifetimeExceeded,
                            EnrollmentTerminationOutcome.TimedOut, SessionStage.EspDeviceSetup);

            var status = FinalStatusBuilder.Build(StateWith(SessionStage.EspDeviceSetup), args,
                packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.NotNull(status.FailureReason);
            Assert.Contains("maximum", status.FailureReason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Build_failure_reason_picks_up_hello_timeout_when_no_details()
        {
            // When Details are absent and the only failure indicator we can derive is the
            // Hello outcome, surface that — most user-actionable hint we have.
            var state = StateWith(SessionStage.Failed, helloOutcome: "timeout");
            var status = FinalStatusBuilder.Build(state,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed),
                packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.NotNull(status.FailureReason);
            Assert.Contains("Hello", status.FailureReason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Build_failure_reason_null_for_success()
        {
            // Healthy outcome → no banner, no failureReason in JSON (NullValueHandling.Ignore).
            var status = FinalStatusBuilder.Build(StateWith(SessionStage.Completed),
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: null, agentStartTimeUtc: StartUtc);
            Assert.Null(status.FailureReason);
        }

        [Fact]
        public void Build_per_app_error_fields_only_set_on_failed_apps()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var list = new AppPackageStateList(logger);

            var failed = new AppPackageState("acme-vpn", 0);
            failed.UpdateState(AppInstallationState.Installing);
            failed.SetErrorContext("IME-MSI-1603", "MSI exited with 1603 — install path locked.", "1603");
            failed.UpdateState(AppInstallationState.Error);
            TestHelpers.SetTargeted(failed, AppTargeted.Device);
            list.Add(failed);

            var ok = new AppPackageState("happy-app", 1);
            ok.UpdateState(AppInstallationState.Installing);
            ok.UpdateState(AppInstallationState.Installed);
            TestHelpers.SetTargeted(ok, AppTargeted.Device);
            list.Add(ok);

            var status = FinalStatusBuilder.Build(StateWith(SessionStage.Failed),
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed),
                packageStates: list, agentStartTimeUtc: StartUtc);

            var failedInfo = status.PackageStatesByPhase["Device"].Single(p => p.AppName == "acme-vpn");
            Assert.Equal("IME-MSI-1603", failedInfo.ErrorPatternId);
            Assert.Equal("MSI exited with 1603 — install path locked.", failedInfo.ErrorDetail);
            Assert.Equal("1603", failedInfo.ErrorCode);

            var okInfo = status.PackageStatesByPhase["Device"].Single(p => p.AppName == "happy-app");
            Assert.Null(okInfo.ErrorPatternId);
            Assert.Null(okInfo.ErrorDetail);
            Assert.Null(okInfo.ErrorCode);
        }

        [Fact]
        public void Build_signal_timestamps_carry_iso8601_when_observed()
        {
            // V1 wrote signalTimestamps; V2 lost it; schema 2 reinstates it for diagnostics.
            // Field engineers reading final-status.json post-mortem rely on these timestamps
            // to reconstruct the session timeline.
            var initial = DecisionState.CreateInitial("S1", "T1");
            var stamped = new DecisionStateBuilder(initial)
            {
                Stage = SessionStage.Completed,
                EspFinalExitUtc = new SignalFact<DateTime>(StartUtc.AddMinutes(5), sourceSignalOrdinal: 1),
                DesktopArrivedUtc = new SignalFact<DateTime>(StartUtc.AddMinutes(7), sourceSignalOrdinal: 2),
            }.Build();

            var status = FinalStatusBuilder.Build(stamped,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed),
                packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.NotNull(status.SignalTimestamps);
            Assert.Equal(StartUtc.AddMinutes(5).ToString("o"), status.SignalTimestamps!["espFinalExit"]);
            Assert.Equal(StartUtc.AddMinutes(7).ToString("o"), status.SignalTimestamps["desktopArrived"]);
        }

        [Fact]
        public void Build_signal_timestamps_null_when_no_signal_observed()
        {
            // No observed signals → null map → JSON omits the field entirely
            // (NullValueHandling.Ignore on the property).
            var status = FinalStatusBuilder.Build(StateWith(SessionStage.Failed),
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed),
                packageStates: null, agentStartTimeUtc: StartUtc);

            Assert.Null(status.SignalTimestamps);
        }
    }

    /// <summary>Mini helper for reflection-based field setters — AppPackageState.Targeted is a private-set property.</summary>
    internal static class TestHelpers
    {
        public static void SetTargeted(AppPackageState pkg, AppTargeted targeted)
        {
            var prop = typeof(AppPackageState).GetProperty(nameof(AppPackageState.Targeted))!;
            prop.SetValue(pkg, targeted);
        }
    }
}
