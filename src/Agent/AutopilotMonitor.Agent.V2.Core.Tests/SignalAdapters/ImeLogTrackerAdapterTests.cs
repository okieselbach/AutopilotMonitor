using System;
using System.Collections.Generic;
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
    public sealed class ImeLogTrackerAdapterTests
    {

        [Fact]
        public void EspPhaseChanged_first_phase_emits_decision_signal_with_phase_in_payload()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            var decisionPost = Assert.Single(f.DecisionSignals(DecisionSignalKind.EspPhaseChanged));
            Assert.Equal("ImeLogTracker", decisionPost.SourceOrigin);
            Assert.Equal("DeviceSetup", decisionPost.Payload![SignalPayloadKeys.EspPhase]);
        }

        [Theory]
        // Known raw phases → mapped to matching EnrollmentPhase enum string on info-event payload.
        // Mapping table lives in ImeLogTrackerAdapter.MapEspPhaseToEnrollmentPhase (near the end
        // of the file); this Theory is the regression guard against drift in that table.
        [InlineData("DeviceSetup", "DeviceSetup")]
        [InlineData("AccountSetup", "AccountSetup")]
        [InlineData("FinalizingSetup", "FinalizingSetup")]
        [InlineData("Finalizing", "FinalizingSetup")] // V1 legacy alias → same enum
        [InlineData("Complete", "Complete")]
        // Unknown raw phases → omit the "phase" key entirely so the event downstream defaults
        // to EnrollmentPhase.Unknown (feedback_phase_strategy).
        [InlineData("SomethingElse", null)]
        [InlineData("preboot", null)]
        public void EspPhaseChanged_info_event_maps_raw_phase_to_enrollment_phase_enum(
            string rawPhase, string? expectedMappedPhase)
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest(rawPhase);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.EspPhaseChanged));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal(rawPhase, info.Payload["espPhase"]);
            if (expectedMappedPhase is null)
                Assert.False(info.Payload.ContainsKey("phase"));
            else
                Assert.Equal(expectedMappedPhase, info.Payload["phase"]);
        }

        [Fact]
        public void EspPhaseChanged_same_phase_repeated_is_deduped_for_both_rails()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            Assert.Single(f.DecisionSignals(DecisionSignalKind.EspPhaseChanged));
            Assert.Single(f.InfoEvents(SharedEventTypes.EspPhaseChanged));
        }

        [Fact]
        public void EspPhaseChanged_distinct_phases_emit_separate_signals_and_info_events()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("AccountSetup");
            adapter.TriggerEspPhaseFromTest("AccountSetup");  // dedup
            adapter.TriggerEspPhaseFromTest("FinalizingSetup");

            var decisions = f.DecisionSignals(DecisionSignalKind.EspPhaseChanged);
            Assert.Equal(3, decisions.Count);
            Assert.Equal("DeviceSetup", decisions[0].Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("AccountSetup", decisions[1].Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("FinalizingSetup", decisions[2].Payload![SignalPayloadKeys.EspPhase]);

            var infos = f.InfoEvents(SharedEventTypes.EspPhaseChanged);
            Assert.Equal(3, infos.Count);
        }

        [Fact]
        public void EspPhaseChanged_null_or_empty_phase_is_skipped()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest(null!);
            adapter.TriggerEspPhaseFromTest("");

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void UserSessionCompleted_emits_decision_signal_fire_once()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerUserSessionCompletedFromTest();
            adapter.TriggerUserSessionCompletedFromTest();

            Assert.Single(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
        }

        [Fact]
        public void UserSessionCompleted_also_emits_ime_user_session_completed_info_event()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerUserSessionCompletedFromTest();

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ImeUserSessionCompleted));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.True(info.Payload.ContainsKey("detectedAt"));
        }

        [Theory]
        [InlineData(AppInstallationState.Installed, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Skipped, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Postponed, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Error, DecisionSignalKind.AppInstallFailed)]
        public void AppStateChange_to_terminal_state_emits_correct_decision_signal_kind(
            AppInstallationState newState, DecisionSignalKind expectedKind)
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState($"app-{newState}", listPos: 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, newState);

            var posted = Assert.Single(f.DecisionSignals(expectedKind));
            Assert.Equal($"app-{newState}", posted.Payload!["appId"]);
            Assert.Equal(newState.ToString(), posted.Payload["newState"]);
        }

        [Theory]
        [InlineData(AppInstallationState.Installed, "app_install_completed")]
        [InlineData(AppInstallationState.Skipped, "app_install_completed")]
        [InlineData(AppInstallationState.Postponed, "app_install_completed")]
        [InlineData(AppInstallationState.Error, "app_install_failed")]
        [InlineData(AppInstallationState.Installing, "app_install_started")]
        [InlineData(AppInstallationState.InProgress, "app_install_started")]
        public void AppStateChange_transition_emits_matching_informational_event_type(
            AppInstallationState newState, string expectedEventType)
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState($"app-{newState}", listPos: 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, newState);

            var info = Assert.Single(f.InfoEvents(expectedEventType));
            Assert.Equal($"app-{newState}", info.Payload!["appId"]);
            Assert.Equal(newState.ToString(), info.Payload["state"]);
        }

        [Fact]
        public void AppStateChange_into_Downloading_emits_app_download_started()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-a", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Downloading);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.AppDownloadStarted));
            Assert.Equal("app-a", info.Payload!["appId"]);
            Assert.Equal(EventSeverity.Info.ToString(), info.Payload[SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void AppStateChange_Downloading_to_Downloading_emits_download_progress_debug()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-a", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Downloading);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.DownloadProgress));
            Assert.Equal(EventSeverity.Debug.ToString(), info.Payload![SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void AppStateChange_to_Unknown_or_NotInstalled_emits_nothing()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-x", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Unknown);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.NotInstalled);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void AppStateChange_terminal_decision_signal_is_fire_once_per_app()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-1", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);
            // Even if the tracker re-fires (shouldn't happen, but defend) — decision signal stays at one.
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installed, AppInstallationState.Error);

            var completions = f.DecisionSignals(DecisionSignalKind.AppInstallCompleted);
            var failures = f.DecisionSignals(DecisionSignalKind.AppInstallFailed);
            Assert.Single(completions);
            Assert.Empty(failures);
        }

        [Fact]
        public void AppStateChange_different_apps_emit_independent_decision_signals()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0), AppInstallationState.Installing, AppInstallationState.Installed);
            adapter.TriggerAppStateFromTest(new AppPackageState("b", 1), AppInstallationState.Installing, AppInstallationState.Error);
            adapter.TriggerAppStateFromTest(new AppPackageState("c", 2), AppInstallationState.Installing, AppInstallationState.Skipped);

            var completions = f.DecisionSignals(DecisionSignalKind.AppInstallCompleted);
            var failures = f.DecisionSignals(DecisionSignalKind.AppInstallFailed);
            Assert.Equal(2, completions.Count);
            Assert.Single(failures);
            Assert.Contains(completions, p => p.Payload!["appId"] == "a");
            Assert.Contains(completions, p => p.Payload!["appId"] == "c");
            Assert.Equal("b", failures[0].Payload!["appId"]);
        }

        [Fact]
        public void AppStateChange_null_app_is_ignored()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerAppStateFromTest(null!, AppInstallationState.Installing, AppInstallationState.Installed);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void AppStateChange_payload_carries_V1_compatible_fields()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            // Build a fully-populated package via Restore to exercise all optional fields.
            var app = AppPackageState.Restore(
                id: "app-xyz",
                listPos: 0,
                name: "Company Portal",
                runAs: AppRunAs.System,
                intent: AppIntent.Install,
                targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: AppInstallationState.Installed,
                downloadingOrInstallingSeen: true,
                progressPercent: 100,
                bytesDownloaded: 100,
                bytesTotal: 100,
                errorPatternId: null,
                errorDetail: null,
                errorCode: null,
                exitCode: null,
                hresultFromWin32: null,
                appVersion: "11.2.1787.0",
                appType: "WinGet",
                attemptNumber: 1,
                detectionResult: "Detected");

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.AppInstallComplete));
            Assert.Equal("app-xyz", info.Payload!["appId"]);
            Assert.Equal("Company Portal", info.Payload["appName"]);
            Assert.Equal("Installed", info.Payload["state"]);
            Assert.Equal("Install", info.Payload["intent"]);
            Assert.Equal("Device", info.Payload["targeted"]);
            Assert.Equal("System", info.Payload["runAs"]);
            Assert.Equal("100", info.Payload["progressPercent"]);
            Assert.Equal("100", info.Payload["bytesDownloaded"]);
            Assert.Equal("100", info.Payload["bytesTotal"]);
            Assert.Equal("false", info.Payload["isError"]);
            Assert.Equal("true", info.Payload["isCompleted"]);
            Assert.Equal("11.2.1787.0", info.Payload["appVersion"]);
            Assert.Equal("WinGet", info.Payload["appType"]);
            Assert.Equal("Detected", info.Payload["detectionResult"]);
        }

        [Fact]
        public void AppStateChange_Error_payload_carries_error_fields()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = AppPackageState.Restore(
                id: "app-err",
                listPos: 0,
                name: "Failing App",
                runAs: AppRunAs.System,
                intent: AppIntent.Install,
                targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: AppInstallationState.Error,
                downloadingOrInstallingSeen: true,
                progressPercent: 0,
                bytesDownloaded: 0,
                bytesTotal: 0,
                errorPatternId: "IME-ERROR-UNMAPPED-EXIT",
                errorDetail: "Admin did NOT set mapping for lpExitCode: 60001",
                errorCode: "60001",
                exitCode: "60001",
                hresultFromWin32: "-2146964895",
                attemptNumber: 1,
                detectionResult: "NotDetected");

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Error);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.AppInstallFailed));
            Assert.Equal("true", info.Payload!["isError"]);
            Assert.Equal("IME-ERROR-UNMAPPED-EXIT", info.Payload["errorPatternId"]);
            Assert.Equal("60001", info.Payload["errorCode"]);
            Assert.Equal("-2146964895", info.Payload["hresultFromWin32"]);
            Assert.Equal(EventSeverity.Error.ToString(), info.Payload[SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void Adapter_preserves_prior_Action_handlers_chain_invoke()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            int priorEspCalls = 0;
            int priorUserCalls = 0;
            f.Tracker.OnEspPhaseChanged = _ => priorEspCalls++;
            f.Tracker.OnUserSessionCompleted = () => priorUserCalls++;

            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // Call through the tracker's Action (simulating what the tracker would do).
            f.Tracker.OnEspPhaseChanged("DeviceSetup");
            f.Tracker.OnUserSessionCompleted();

            Assert.Equal(1, priorEspCalls);
            Assert.Equal(1, priorUserCalls);
            // 2 DecisionSignals + 2 InformationalEvents = 4 total.
            Assert.Equal(4, f.Ingress.Posted.Count);
        }

        [Fact]
        public void Dispose_restores_prior_Action_handlers()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            int priorCalls = 0;
            Action<string> priorAction = _ => priorCalls++;
            f.Tracker.OnEspPhaseChanged = priorAction;

            var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            Assert.NotSame(priorAction, f.Tracker.OnEspPhaseChanged);

            adapter.Dispose();
            Assert.Same(priorAction, f.Tracker.OnEspPhaseChanged);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(f.Tracker, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(f.Tracker, f.Ingress, null!));
        }

        // -- 5.9b: OnImeAgentVersion / OnDoTelemetryReceived / OnScriptCompleted --------

        [Fact]
        public void ImeAgentVersion_emits_ime_agent_version_info_event()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerImeAgentVersionFromTest("1.101.109.0");

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ImeAgentVersion));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("1.101.109.0", info.Payload["agentVersion"]);
            Assert.Equal("IME Agent version: 1.101.109.0", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ImeAgentVersion_null_or_empty_is_skipped()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerImeAgentVersionFromTest(null!);
            adapter.TriggerImeAgentVersionFromTest("");

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void ImeAgentVersion_same_version_twice_is_not_deduped_matches_V1_behavior()
        {
            // V1 reference session shows 2 ime_agent_version events for the same version
            // in the same session (seq 24 and 60). Adapter must not dedup.
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerImeAgentVersionFromTest("1.101.109.0");
            adapter.TriggerImeAgentVersionFromTest("1.101.109.0");

            Assert.Equal(2, f.InfoEvents(SharedEventTypes.ImeAgentVersion).Count);
        }

        [Fact]
        public void DoTelemetry_emits_do_telemetry_info_event_with_do_fields()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var app = AppPackageState.Restore(
                id: "0e3be4ce-790b-4081-831c-d72d92f3cc9b",
                listPos: 0,
                name: "SAP 2024 Patch 13",
                runAs: AppRunAs.System,
                intent: AppIntent.Install,
                targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: AppInstallationState.Downloading,
                downloadingOrInstallingSeen: true,
                progressPercent: 97,
                bytesDownloaded: 185_023_504,
                bytesTotal: 190_266_384,
                doFileSize: 190_266_384,
                doTotalBytesDownloaded: 190_266_384,
                doBytesFromPeers: 0,
                doPercentPeerCaching: 0,
                doBytesFromHttp: 190_266_384,
                doDownloadMode: 1,
                doDownloadDuration: "00:01:45.159",
                hasDoTelemetry: true);

            adapter.TriggerDoTelemetryFromTest(app);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.DoTelemetry));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal(app.Id, info.Payload["appId"]);
            Assert.Equal("SAP 2024 Patch 13", info.Payload["appName"]);
            Assert.Equal("Downloading", info.Payload["state"]);
            Assert.Equal("97", info.Payload["progressPercent"]);
            Assert.Equal("185023504", info.Payload["bytesDownloaded"]);
            Assert.Equal("190266384", info.Payload["doFileSize"]);
            Assert.Equal("0", info.Payload["doBytesFromPeers"]);
            Assert.Equal("190266384", info.Payload["doBytesFromHttp"]);
            Assert.Equal("1", info.Payload["doDownloadMode"]);
            Assert.Equal("00:01:45.159", info.Payload["doDownloadDuration"]);
        }

        [Fact]
        public void DoTelemetry_null_app_or_empty_id_is_ignored()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerDoTelemetryFromTest(null!);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void ScriptCompleted_platform_success_emits_script_completed_info_source_ImeLogTracker()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var script = new ScriptExecutionState
            {
                PolicyId = "4453d1ad-8171-4459-ae6b-ad97f722ea11",
                ScriptType = "platform",
                ExitCode = 0,
                RunContext = "System",
                Result = "Success",
                Stdout = "Script created at: C:\\ProgramData\\TPF IT\\ShowItemsOnDesktop\\ShowItemsOnDesktop.ps1",
            };

            adapter.TriggerScriptCompletedFromTest(script);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            // Source uniformly reports the producing adapter; consistent with sibling
            // events (app_install_started, download_progress, do_telemetry, …).
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal(script.PolicyId, info.Payload["policyId"]);
            Assert.Equal("platform", info.Payload["scriptType"]);
            Assert.Equal("0", info.Payload["exitCode"]);
            Assert.Equal("System", info.Payload["runContext"]);
            Assert.Equal("Success", info.Payload["result"]);
            Assert.Equal(EventSeverity.Info.ToString(), info.Payload[SignalPayloadKeys.Severity]);
            Assert.Contains("Platform script 4453d1ad: Success (exit: 0)", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ScriptCompleted_platform_failure_emits_script_failed_eventType_with_severity_Error()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var script = new ScriptExecutionState
            {
                PolicyId = "deadbeef-0000-0000-0000-000000000000",
                ScriptType = "platform",
                ExitCode = 1,
                RunContext = "System",
                Result = "Failed",
                Stderr = "Script failed: access denied",
            };

            adapter.TriggerScriptCompletedFromTest(script);

            // V1 parity: failures get a distinct eventType (script_failed) so the Web reducer
            // — which keys on eventType, not severity — renders them as Failed rows. Without
            // this, V2 failures would appear as green "Success" cards in the UI.
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptFailed));
            Assert.Equal(EventSeverity.Error.ToString(), info.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("Failed", info.Payload["result"]);
            Assert.Equal("1", info.Payload["exitCode"]);
        }

        [Fact]
        public void ScriptCompleted_remediation_carries_compliance_result()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var script = new ScriptExecutionState
            {
                PolicyId = "cafebabe-0000-0000-0000-000000000000",
                ScriptType = "remediation",
                ScriptPart = "detection",
                ExitCode = 0,
                RunContext = "System",
                ComplianceResult = "True",
            };

            adapter.TriggerScriptCompletedFromTest(script);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("remediation", info.Payload!["scriptType"]);
            Assert.Equal("detection", info.Payload["scriptPart"]);
            Assert.Equal("True", info.Payload["complianceResult"]);
            Assert.Contains("Remediation script cafebabe: compliance=True", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ScriptCompleted_null_or_empty_policyId_is_ignored()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(null!);
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState { PolicyId = null });
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState { PolicyId = "" });

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void New_callbacks_preserve_prior_action_handlers_chain_invoke()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            int priorVersionCalls = 0;
            int priorDoCalls = 0;
            int priorScriptCalls = 0;
            f.Tracker.OnImeAgentVersion = _ => priorVersionCalls++;
            f.Tracker.OnDoTelemetryReceived = _ => priorDoCalls++;
            f.Tracker.OnScriptCompleted = _ => priorScriptCalls++;

            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.OnImeAgentVersion("1.0.0.0");
            f.Tracker.OnDoTelemetryReceived(new AppPackageState("app-1", 0));
            f.Tracker.OnScriptCompleted(new ScriptExecutionState { PolicyId = "p", ScriptType = "platform" });

            Assert.Equal(1, priorVersionCalls);
            Assert.Equal(1, priorDoCalls);
            Assert.Equal(1, priorScriptCalls);
            Assert.Equal(3, f.AllInfoEvents().Count);
        }

        [Fact]
        public void Dispose_restores_all_prior_action_handlers()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            Action<string> priorVersion = _ => { };
            Action<AppPackageState> priorDo = _ => { };
            Action<ScriptExecutionState> priorScript = _ => { };
            f.Tracker.OnImeAgentVersion = priorVersion;
            f.Tracker.OnDoTelemetryReceived = priorDo;
            f.Tracker.OnScriptCompleted = priorScript;

            var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            Assert.NotSame(priorVersion, f.Tracker.OnImeAgentVersion);
            Assert.NotSame(priorDo, f.Tracker.OnDoTelemetryReceived);
            Assert.NotSame(priorScript, f.Tracker.OnScriptCompleted);

            adapter.Dispose();
            Assert.Same(priorVersion, f.Tracker.OnImeAgentVersion);
            Assert.Same(priorDo, f.Tracker.OnDoTelemetryReceived);
            Assert.Same(priorScript, f.Tracker.OnScriptCompleted);
        }

        [Theory]
        // V1 parity: terminal Installed/Error transitions must also emit a shadow
        // `download_progress` event with status=completed/failed so the Web UI's
        // DownloadProgress panel closes out apps that produced no byte progress
        // (WinGet/Store apps emit no DO byte data — without the shadow they'd
        // stay stuck on "started" forever).
        [InlineData(AppInstallationState.Installed, SharedEventTypes.AppInstallComplete, "completed")]
        [InlineData(AppInstallationState.Error, SharedEventTypes.AppInstallFailed, "failed")]
        public void AppStateChange_terminal_state_emits_V1_parity_shadow_download_progress(
            AppInstallationState terminalState,
            string expectedPrimaryEventType,
            string expectedShadowStatus)
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = AppPackageState.Restore(
                id: "wg-app",
                listPos: 0,
                name: "Company Portal",
                runAs: AppRunAs.System,
                intent: AppIntent.Install,
                targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: terminalState,
                downloadingOrInstallingSeen: true,
                progressPercent: terminalState == AppInstallationState.Installed ? 100 : 0,
                bytesDownloaded: terminalState == AppInstallationState.Installed ? 100 : 0,
                bytesTotal: 100,
                errorPatternId: null,
                errorDetail: terminalState == AppInstallationState.Error ? "synthetic" : null,
                errorCode: terminalState == AppInstallationState.Error ? "1" : null,
                exitCode: null,
                hresultFromWin32: null,
                appType: "WinGet");

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, terminalState);

            // Primary terminal event still fires once (unchanged).
            Assert.Single(f.InfoEvents(expectedPrimaryEventType));

            // Shadow `download_progress` carries status + the same payload fields.
            var shadow = Assert.Single(f.InfoEvents(SharedEventTypes.DownloadProgress));
            Assert.Equal(expectedShadowStatus, shadow.Payload!["status"]);
            Assert.Equal("wg-app", shadow.Payload["appId"]);
            Assert.Equal("Company Portal", shadow.Payload["appName"]);
            Assert.Equal(EventSeverity.Debug.ToString(), shadow.Payload[SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void AppStateChange_non_terminal_transitions_do_not_emit_shadow_download_progress()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            AppPackageState MakeApp(AppInstallationState state) => AppPackageState.Restore(
                id: "app-1",
                listPos: 0,
                name: "App",
                runAs: AppRunAs.System,
                intent: AppIntent.Install,
                targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: state,
                downloadingOrInstallingSeen: true,
                progressPercent: 0,
                bytesDownloaded: 0,
                bytesTotal: 0,
                errorPatternId: null,
                errorDetail: null,
                errorCode: null,
                exitCode: null,
                hresultFromWin32: null);

            // Downloading → Installing must NOT add a shadow `download_progress` —
            // and Skipped/Postponed terminal states must NOT either (V1 parity).
            adapter.TriggerAppStateFromTest(MakeApp(AppInstallationState.Downloading), AppInstallationState.Unknown, AppInstallationState.Downloading);
            adapter.TriggerAppStateFromTest(MakeApp(AppInstallationState.Installing), AppInstallationState.Downloading, AppInstallationState.Installing);
            adapter.TriggerAppStateFromTest(MakeApp(AppInstallationState.Skipped), AppInstallationState.Installing, AppInstallationState.Skipped);
            adapter.TriggerAppStateFromTest(MakeApp(AppInstallationState.Postponed), AppInstallationState.Installing, AppInstallationState.Postponed);

            Assert.Empty(f.InfoEvents(SharedEventTypes.DownloadProgress));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Health-script live-progress + atomic-result tests (PR-HS1/HS4)
        //
        // The HS-NEW-RESULT pattern delivers the full pre-detection / remediation /
        // post-detection JSON in one shot. HS-SCRIPT-START fires earlier and drives
        // a live `script_started` event so the UI can show a "running" indicator
        // before the consolidated final result arrives ~30 s – 3 min later.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ScriptStarted_remediation_emits_live_script_started_event()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptStartedFromTest(new ScriptStartedInfo
            {
                PolicyId = "75d14a95-d49f-473d-9d65-d4b006bc7468",
                ScriptType = "remediation",
                PolicyType = "6",
            });

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptStarted));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("75d14a95-d49f-473d-9d65-d4b006bc7468", info.Payload["policyId"]);
            Assert.Equal("remediation", info.Payload["scriptType"]);
            Assert.Equal("6", info.Payload["policyType"]);
            Assert.Contains("Health script 75d14a95: started", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ScriptStarted_null_or_empty_policyId_is_ignored()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptStartedFromTest(null!);
            adapter.TriggerScriptStartedFromTest(new ScriptStartedInfo { PolicyId = null });
            adapter.TriggerScriptStartedFromTest(new ScriptStartedInfo { PolicyId = "" });

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void ScriptCompleted_remediation_post_detection_part_propagates_through_adapter()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var script = new ScriptExecutionState
            {
                PolicyId = "cafebabe-0000-0000-0000-000000000000",
                ScriptType = "remediation",
                ScriptPart = "post-detection",
                ExitCode = 0,
                RunContext = "System",
                ComplianceResult = "True",
                RemediationStatus = 2, // Remediated
                TargetType = 2,         // Device
                ErrorCode = 0,
            };

            adapter.TriggerScriptCompletedFromTest(script);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("post-detection", info.Payload!["scriptPart"]);
            Assert.Equal("True", info.Payload["complianceResult"]);
            // Wire-format check: the new RemediationStatus / TargetType / ErrorCode fields
            // must reach the UI via the event payload, otherwise the detect-only badge,
            // status pills, and Target column in the detail panel can never light up.
            Assert.Equal("2", info.Payload["remediationStatus"]);
            Assert.Equal("2", info.Payload["targetType"]);
            Assert.Equal("0", info.Payload["errorCode"]);
        }

        [Fact]
        public void HealthScriptResult_propagates_RemediationStatus_TargetType_ErrorCode_to_event_payload()
        {
            // End-to-end: HandleHealthScriptResultJson → adapter → event payload. Detection-only
            // policy from c1e714e6 with TargetType=2 (Device) and RemediationStatus=4 (NoRemediation).
            const string json = @"{
              ""PolicyId"": ""75d14a95-d49f-473d-9d65-d4b006bc7468"",
              ""PreRemediationDetectScriptOutput"": ""LocalAdminIsEnabled=False"",
              ""RemediationStatus"": 4,
              ""ErrorCode"": 0,
              ""Info"": { ""FirstDetectExitCode"": 0, ""ErrorDetails"": null },
              ""TargetType"": 2,
              ""RunAsAccount"": 1
            }";

            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.HandleHealthScriptResultJson(json);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("4", info.Payload!["remediationStatus"]);
            Assert.Equal("2", info.Payload["targetType"]);
            Assert.Equal("0", info.Payload["errorCode"]);
            Assert.Equal("System", info.Payload["runContext"]);
        }

        [Fact]
        public void HealthScriptResult_DetectionOnly_Compliant_emits_one_detection_event()
        {
            // Real JSON sample from session c1e714e6 (LocalAdmin compliance check).
            // Detection-only policy: no remediation script attached → all
            // RemediationScript* / PostRemediationDetectScript* fields are null
            // and RemediationStatus = 4 (NoRemediation).
            const string json = @"{
              ""PolicyId"": ""75d14a95-d49f-473d-9d65-d4b006bc7468"",
              ""UserId"": ""00000000-0000-0000-0000-000000000000"",
              ""Result"": 3,
              ""ResultType"": 1,
              ""ErrorCode"": 0,
              ""PreRemediationDetectScriptOutput"": ""LocalAdminIsEnabled=False, action list: Administrator|+Administrator,-"",
              ""PreRemediationDetectScriptError"": """",
              ""RemediationScriptOutputDetails"": null,
              ""RemediationScriptErrorDetails"": null,
              ""PostRemediationDetectScriptOutput"": null,
              ""PostRemediationDetectScriptError"": null,
              ""RemediationStatus"": 4,
              ""Info"": {
                ""RemediationExitCode"": null,
                ""FirstDetectExitCode"": 0,
                ""LastDetectExitCode"": null,
                ""ErrorDetails"": null
              },
              ""TargetType"": 2,
              ""RunAsAccount"": 1
            }";

            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.HandleHealthScriptResultJson(json);

            var infos = f.InfoEvents(SharedEventTypes.ScriptCompleted);
            var single = Assert.Single(infos);
            Assert.Equal("75d14a95-d49f-473d-9d65-d4b006bc7468", single.Payload!["policyId"]);
            Assert.Equal("remediation", single.Payload["scriptType"]);
            Assert.Equal("detection", single.Payload["scriptPart"]);
            Assert.Equal("True", single.Payload["complianceResult"]);
            Assert.Equal("0", single.Payload["exitCode"]);
            Assert.Equal("System", single.Payload["runContext"]);
        }

        [Fact]
        public void HealthScriptResult_DetectionOnly_NonCompliant_emits_one_completed_event_with_complianceFalse()
        {
            // Real JSON shape from dev-machine HealthScripts.log (BuiltInAdmin policy 7980b14e):
            // FirstDetectExitCode = 1, RemediationStatus = 4 (NoRemediation = no remediation
            // script attached), all Remediation* / Post* fields null. Phase-aware semantics:
            // detection exit != 0 is a compliance verdict, not a script failure → event type
            // is script_completed with complianceResult="False". UI styles this row amber via
            // isNonCompliantReport() and surfaces the detect-only badge from RemediationStatus=4.
            const string json = @"{
              ""PolicyId"": ""7980b14e-0e5a-48c7-a8e5-d6018407ca22"",
              ""UserId"": ""0234b7f4-f66c-4f82-a4e3-67411f22ba40"",
              ""Result"": 4,
              ""PreRemediationDetectScriptOutput"": ""BuiltInAdminEnabled=False"",
              ""PreRemediationDetectScriptError"": """",
              ""RemediationScriptOutputDetails"": null,
              ""RemediationScriptErrorDetails"": null,
              ""PostRemediationDetectScriptOutput"": null,
              ""PostRemediationDetectScriptError"": null,
              ""RemediationStatus"": 4,
              ""Info"": {
                ""RemediationExitCode"": null,
                ""FirstDetectExitCode"": 1,
                ""LastDetectExitCode"": null,
                ""ErrorDetails"": null
              },
              ""TargetType"": 1,
              ""RunAsAccount"": 1
            }";

            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.HandleHealthScriptResultJson(json);

            // Phase-aware routing: detection exit != 0 is a non-compliant compliance report,
            // NOT a script crash. The script ran perfectly and reported the policy is in a
            // non-compliant state. Cycle-level outcome lives in RemediationStatus (here = 4
            // / NoRemediation = detect-only). Event type stays script_completed so metrics
            // and the timeline don't render this as a failure.
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptFailed));
            var single = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("7980b14e-0e5a-48c7-a8e5-d6018407ca22", single.Payload!["policyId"]);
            Assert.Equal("detection", single.Payload["scriptPart"]);
            Assert.Equal("False", single.Payload["complianceResult"]);
            Assert.Equal("1", single.Payload["exitCode"]);
            Assert.Equal("4", single.Payload["remediationStatus"]);
            Assert.Equal(EventSeverity.Info.ToString(), single.Payload[SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void HealthScriptResult_FullCycle_emits_three_events()
        {
            // Hand-constructed JSON modelling a full health-script cycle: pre-detection False
            // → remediation script ran (exit 0) → post-detection now True. The handler must
            // emit three ScriptCompleted events with scriptPart = detection / remediation /
            // post-detection respectively.
            const string json = @"{
              ""PolicyId"": ""11111111-2222-3333-4444-555555555555"",
              ""UserId"": ""user-1"",
              ""Result"": 2,
              ""ErrorCode"": 0,
              ""PreRemediationDetectScriptOutput"": ""needs fix"",
              ""PreRemediationDetectScriptError"": """",
              ""RemediationScriptOutputDetails"": ""applied fix"",
              ""RemediationScriptErrorDetails"": """",
              ""PostRemediationDetectScriptOutput"": ""verified ok"",
              ""PostRemediationDetectScriptError"": """",
              ""RemediationStatus"": 2,
              ""Info"": {
                ""RemediationExitCode"": 0,
                ""FirstDetectExitCode"": 1,
                ""LastDetectExitCode"": 0,
                ""ErrorDetails"": null
              },
              ""TargetType"": 2,
              ""RunAsAccount"": 1
            }";

            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.HandleHealthScriptResultJson(json);

            // Phase-aware routing: ALL three phases of a successfully-remediated cycle emit
            // script_completed. The detection's exit 1 is a non-compliance report (script ran
            // fine), the remediation succeeded (exit 0), and the post-detection confirms the
            // fix took. None of these are "script failures" — the cycle is healthy.
            Assert.Empty(f.InfoEvents(SharedEventTypes.ScriptFailed));
            var completed = f.InfoEvents(SharedEventTypes.ScriptCompleted);
            Assert.Equal(3, completed.Count);

            var detection = completed.Single(e => e.Payload!["scriptPart"] == "detection");
            Assert.Equal("False", detection.Payload!["complianceResult"]);
            Assert.Equal("1", detection.Payload["exitCode"]);
            Assert.Equal("needs fix", detection.Payload["stdout"]);
            Assert.Equal(EventSeverity.Info.ToString(), detection.Payload[SignalPayloadKeys.Severity]);

            var remediation = completed.Single(e => e.Payload!["scriptPart"] == "remediation");
            Assert.Equal("0", remediation.Payload!["exitCode"]);
            Assert.Equal("applied fix", remediation.Payload["stdout"]);
            Assert.False(remediation.Payload.ContainsKey("complianceResult"),
                "remediation phase must not carry a compliance verdict");

            var post = completed.Single(e => e.Payload!["scriptPart"] == "post-detection");
            Assert.Equal("True", post.Payload!["complianceResult"]);
            Assert.Equal("0", post.Payload["exitCode"]);
            Assert.Equal("verified ok", post.Payload["stdout"]);
        }

        [Fact]
        public void HealthScriptResult_remediation_phase_with_non_zero_exit_emits_script_failed()
        {
            // Phase-aware routing flip side: when the actual remediation script crashes
            // (RemediationExitCode != 0), THAT phase emits script_failed because it's a
            // genuine script failure, not a compliance report.
            const string json = @"{
              ""PolicyId"": ""22222222-2222-2222-2222-222222222222"",
              ""PreRemediationDetectScriptOutput"": ""needs fix"",
              ""RemediationScriptOutputDetails"": ""attempted fix"",
              ""RemediationScriptErrorDetails"": ""error: access denied"",
              ""RemediationStatus"": 3,
              ""Info"": {
                ""FirstDetectExitCode"": 1,
                ""RemediationExitCode"": 1,
                ""LastDetectExitCode"": null,
                ""ErrorDetails"": ""Remediation crashed""
              },
              ""TargetType"": 2,
              ""RunAsAccount"": 1
            }";

            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.HandleHealthScriptResultJson(json);

            // Detection: non-compliant report → script_completed (phase-aware exemption)
            var detection = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("detection", detection.Payload!["scriptPart"]);
            Assert.Equal("False", detection.Payload["complianceResult"]);

            // Remediation: actual script failure → script_failed (phase-aware exemption does NOT apply)
            var remediation = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptFailed));
            Assert.Equal("remediation", remediation.Payload!["scriptPart"]);
            Assert.Equal("1", remediation.Payload["exitCode"]);
            Assert.Equal(EventSeverity.Error.ToString(), remediation.Payload[SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void HealthScriptResult_InvalidJson_increments_failure_counter_and_emits_nothing()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var before = f.Tracker.HealthScriptResultParseFailures;

            f.Tracker.HandleHealthScriptResultJson("{not even valid json");

            Assert.Equal(before + 1, f.Tracker.HealthScriptResultParseFailures);
            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void HealthScriptResult_MissingPolicyId_emits_nothing()
        {
            // Defensive: the regex captured something json-shaped but the payload is missing
            // the required PolicyId. Skip cleanly without emitting half-empty events.
            const string json = @"{
              ""PreRemediationDetectScriptOutput"": ""orphan output"",
              ""RemediationStatus"": 4,
              ""Info"": { ""FirstDetectExitCode"": 0 }
            }";

            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.HandleHealthScriptResultJson(json);

            Assert.Empty(f.Ingress.Posted);
        }
    }
}
