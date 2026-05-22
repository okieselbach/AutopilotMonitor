using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// PR1 — ContinueAnyway-aware ESP terminal-failure defang (Session 4fa5a2d4, 2026-05-22).
    /// Coverage for the advisory path added to <c>HandleEspTerminalFailureV1</c>:
    /// <list type="bullet">
    ///   <item>Advisory-Demote when ESP profile permits ContinueAnyway AND AccountSetup has been entered.</item>
    ///   <item>Fire-once idempotency via <c>EspAdvisoryFailureRecordedUtc</c>.</item>
    ///   <item>Negative paths (ContinueAnyway false / unknown / AccountSetup not entered) still terminate.</item>
    ///   <item>Whitelist-merge of failure-context keys into the audit-trail typed payload (both paths).</item>
    /// </list>
    /// </summary>
    public sealed class EspFailureAdvisoryDefangTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 5, 22, 8, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null,
            string sourceOrigin = "test")
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: sourceOrigin,
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        private static DecisionEffect SingleTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.Single(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        /// <summary>
        /// Builds a state that has ESP-ContinueAnyway observed and AccountSetup entered — the
        /// canonical "advisory eligible" preconditions. Mirrors the production wire: bootstrap
        /// fires EspConfigDetected, ESP transitions to DeviceSetup then AccountSetup before the
        /// late DeviceSetup/Certificates failure registry-write arrives.
        /// </summary>
        private static DecisionState SetupContinueAnywayWithAccountSetupEntered(DecisionEngine engine, bool continueAnyway = true)
        {
            var state = DecisionState.CreateInitial("sess-advisory", "tenant-advisory");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    [SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = "30",
                    [SignalPayloadKeys.EspAllowContinueAnyway] = continueAnyway ? "true" : "false",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                30, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(15),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            return state;
        }

        // =============================================== advisory-defang positive path ====

        [Fact]
        public void EspTerminalFailure_WhenContinueAnywayAndAccountSetupEntered_DemotesToAdvisory()
        {
            var engine = new DecisionEngine();
            var state = SetupContinueAnywayWithAccountSetupEntered(engine);

            var preStage = state.Stage;
            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
                    ["failedSubcategory"] = "Certificates",
                    ["category"] = "DeviceSetup",
                }));

            // No transition to Failed — stage unchanged, no terminal outcome.
            Assert.Equal(preStage, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);

            // The advisory fact is recorded — the fire-once gate for duplicates.
            Assert.NotNull(step.NewState.EspAdvisoryFailureRecordedUtc);
            Assert.Equal(T0.AddMinutes(30), step.NewState.EspAdvisoryFailureRecordedUtc!.Value);
            Assert.Equal(40, step.NewState.EspAdvisoryFailureRecordedUtc!.SourceSignalOrdinal);

            // LastFailureTrigger is NOT set on the advisory path — only the Failed-path
            // EnrollmentTerminationHandler relies on that fact.
            Assert.Null(step.NewState.LastFailureTrigger);

            // Deadlines stay armed — monitoring continues.
            // (We don't assert on a specific deadline name because the test setup doesn't arm one,
            // but the fact that the reducer didn't ClearDeadlines() means the existing list survives.)

            // Single esp_failure_advisory effect, no enrollment_failed.
            var advisory = SingleTimelineEffect(step, "esp_failure_advisory");
            Assert.Equal("esp_terminal_failure", advisory.Parameters!["reason"]);
            Assert.Equal("esp_failure_defanged_continueanyway_with_accountsetup", advisory.Parameters!["advisoryReason"]);
            // Severity is explicit (not derived from "_failed"-suffix DeriveSeverity default,
            // which would give Info for advisory). The UI renders this as amber/warning.
            Assert.Equal("Warning", advisory.Parameters!["severity"]);
            Assert.Equal("Certificates", advisory.Parameters!["failedSubcategory"]);
            Assert.Equal("DeviceSetup", advisory.Parameters!["category"]);
            Assert.Equal("true", advisory.Parameters!["espAllowContinueAnyway"]);
            Assert.Equal("true", advisory.Parameters!["mayHaveContinuedAnyway"]);
            Assert.Contains("AccountSetup", advisory.Parameters!["continueAnywayHint"]);

            Assert.DoesNotContain(step.Effects, e =>
                e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == "enrollment_failed");
        }

        [Fact]
        public void EspTerminalFailure_AdvisoryEffect_TypedPayloadCarriesAuditTrailAndFailureContext()
        {
            var engine = new DecisionEngine();
            var state = SetupContinueAnywayWithAccountSetupEntered(engine);

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
                    ["errorCode"] = "0x87d1041c",
                    ["failedSubcategory"] = "Certificates",
                    ["category"] = "DeviceSetup",
                }));

            var advisory = SingleTimelineEffect(step, "esp_failure_advisory");
            var payload = Assert.IsType<Dictionary<string, object>>(advisory.TypedPayload);

            // Audit-trail anchors present.
            Assert.Equal("DecisionEngine", payload["decisionSource"]);
            Assert.Equal(nameof(DecisionSignalKind.EspTerminalFailure), payload["trigger"]);
            Assert.True(payload.ContainsKey("signalsSeen"));
            Assert.True(payload.ContainsKey("scenario"));

            // Failure-context whitelist merged in (Codex review #18).
            Assert.Equal("Provisioning_DeviceSetup_Certificates_Failed", payload["failureType"]);
            Assert.Equal("0x87d1041c", payload["errorCode"]);
            Assert.Equal("Certificates", payload["failedSubcategory"]);
            Assert.Equal("DeviceSetup", payload["category"]);
            Assert.Equal("true", payload["espAllowContinueAnyway"]);
            Assert.Equal("true", payload["mayHaveContinuedAnyway"]);
            Assert.Equal("esp_failure_defanged_continueanyway_with_accountsetup", payload["advisoryReason"]);

            // Emitter-metadata keys must NOT be in the typed payload (Codex review #18).
            Assert.False(payload.ContainsKey("eventType"));
        }

        // =================================================== idempotency / duplicate ====

        [Fact]
        public void EspTerminalFailure_AfterAdvisory_PureDuplicateDeadEnds()
        {
            // Pure duplicate: ShellCoreTracker fires after ProvisioningStatusTracker already did
            // the rich emission. The ShellCoreTrackerAdapter (line 141) always includes
            // failureType in its payload but never the registry-specific keys (errorCode /
            // failedSubcategory / category) — those come exclusively from the registry path.
            // This test mirrors that production reality so the dead-end gate is exercised
            // against the actual adapter shape, not a strawman "reason-only" payload.
            var engine = new DecisionEngine();
            var state = SetupContinueAnywayWithAccountSetupEntered(engine);

            // First firing — full registry-derived payload.
            state = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
                    ["failedSubcategory"] = "Certificates",
                    ["category"] = "DeviceSetup",
                })).NewState;
            Assert.NotNull(state.EspAdvisoryFailureRecordedUtc);
            var firstAdvisoryUtc = state.EspAdvisoryFailureRecordedUtc!.Value;
            var preStage = state.Stage;
            var preStepIndex = state.StepIndex;

            // Second firing — ShellCoreTracker shape: failureType only, no registry detail.
            var step = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(31),
                new Dictionary<string, string> { ["failureType"] = "ESPProgress_Timeout" }));

            // Dead-end transition, no effects. failureType alone must NOT count as registry
            // enrichment — otherwise ShellCoreTracker would always trigger a redundant
            // follow-up advisory after a rich Provisioning advisory.
            Assert.Equal(preStage, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.Empty(step.Effects);
            Assert.False(step.Transition.Taken);
            Assert.Equal("esp_terminal_failure_advisory_already_recorded", step.Transition.DeadEndReason);

            // Step bookkeeping advanced (signal counted as processed).
            Assert.Equal(preStepIndex + 1, step.NewState.StepIndex);
            Assert.Equal(50, step.NewState.LastAppliedSignalOrdinal);

            // Original advisory fact unchanged — first record wins.
            Assert.Equal(firstAdvisoryUtc, step.NewState.EspAdvisoryFailureRecordedUtc!.Value);
        }

        [Fact]
        public void EspTerminalFailure_AfterAdvisory_ReasonOnlySignalAlsoDeadEnds()
        {
            // Defensive coverage for the rarer case where a duplicate carries only a `reason`
            // string with no failure-context keys at all (e.g. a synthetic test signal or a
            // hypothetical future adapter that doesn't propagate failureType). The dead-end
            // path must still fire.
            var engine = new DecisionEngine();
            var state = SetupContinueAnywayWithAccountSetupEntered(engine);

            state = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
                    ["failedSubcategory"] = "Certificates",
                    ["category"] = "DeviceSetup",
                })).NewState;

            var step = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(31),
                new Dictionary<string, string> { ["reason"] = "esp_terminal_failure_account_setup" }));

            Assert.Empty(step.Effects);
            Assert.False(step.Transition.Taken);
            Assert.Equal("esp_terminal_failure_advisory_already_recorded", step.Transition.DeadEndReason);
        }

        [Fact]
        public void EspTerminalFailure_AfterAdvisory_EnrichmentSignalEmitsFollowupAdvisory()
        {
            // Real-world race: ShellCoreTracker fires first with sparse payload (just `reason`),
            // ProvisioningStatusTracker fires later with full registry-derived detail
            // (failureType/errorCode/failedSubcategory/category). The duplicate-guard must NOT
            // blindly drop the second signal — that would lose the HRESULT and subcategory the
            // UI and KQL filters rely on.
            var engine = new DecisionEngine();
            var state = SetupContinueAnywayWithAccountSetupEntered(engine);

            // First firing — sparse ShellCoreTracker payload.
            state = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string> { ["reason"] = "esp_terminal_failure_account_setup" })).NewState;
            Assert.NotNull(state.EspAdvisoryFailureRecordedUtc);
            var firstAdvisoryUtc = state.EspAdvisoryFailureRecordedUtc!.Value;
            var preStage = state.Stage;

            // Second firing — registry-derived enrichment.
            var step = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(31),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
                    ["errorCode"] = "0x87d1041c",
                    ["failedSubcategory"] = "Certificates",
                    ["category"] = "DeviceSetup",
                }));

            // Taken transition (NOT a dead-end), stage unchanged, advisory anchor untouched.
            Assert.Equal(preStage, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.True(step.Transition.Taken);
            Assert.Equal(firstAdvisoryUtc, step.NewState.EspAdvisoryFailureRecordedUtc!.Value);

            // Follow-up advisory event carries the enrichment payload + distinct advisoryReason.
            var advisory = SingleTimelineEffect(step, "esp_failure_advisory");
            Assert.Equal("esp_failure_advisory_enriched_from_duplicate", advisory.Parameters!["advisoryReason"]);
            Assert.Equal("Warning", advisory.Parameters!["severity"]);
            Assert.Equal("Provisioning_DeviceSetup_Certificates_Failed", advisory.Parameters!["failureType"]);
            Assert.Equal("0x87d1041c", advisory.Parameters!["errorCode"]);
            Assert.Equal("Certificates", advisory.Parameters!["failedSubcategory"]);
            Assert.Equal("DeviceSetup", advisory.Parameters!["category"]);

            // TypedPayload also carries the merged failure context (whitelist via Build helper).
            var payload = Assert.IsType<Dictionary<string, object>>(advisory.TypedPayload);
            Assert.Equal("0x87d1041c", payload["errorCode"]);
            Assert.Equal("esp_failure_advisory_enriched_from_duplicate", payload["advisoryReason"]);
        }

        // ======================================================== negative pathways =====

        [Fact]
        public void EspTerminalFailure_WhenContinueAnywayTrue_ButNoAccountSetupEntered_StillFails()
        {
            // Subset of the SetupContinueAnyway… helper: only stage to DeviceSetup, never AccountSetup.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-no-as", "tenant-no-as");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspAllowContinueAnyway] = "true" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;

            // Sanity: AccountSetup never entered.
            Assert.Null(state.AccountSetupEnteredUtc);

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(15),
                new Dictionary<string, string> { ["failedSubcategory"] = "Certificates", ["category"] = "DeviceSetup" }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            Assert.Null(step.NewState.EspAdvisoryFailureRecordedUtc);

            // Failed-path event emitted, NOT advisory.
            SingleTimelineEffect(step, "enrollment_failed");
        }

        [Fact]
        public void EspTerminalFailure_WhenContinueAnywayFalse_AndAccountSetupEntered_StillFails()
        {
            var engine = new DecisionEngine();
            var state = SetupContinueAnywayWithAccountSetupEntered(engine, continueAnyway: false);

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string> { ["failedSubcategory"] = "Certificates", ["category"] = "DeviceSetup" }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            Assert.Null(step.NewState.EspAdvisoryFailureRecordedUtc);

            SingleTimelineEffect(step, "enrollment_failed");
        }

        [Fact]
        public void EspTerminalFailure_WhenContinueAnywayUnknown_DefaultsToFailedPath()
        {
            // Race-condition coverage: bootstrap probe + DeviceInfoCollector both missed the
            // FirstSync read (very early boot, agent crashed and replayed signals from log).
            // EspAllowContinueAnyway is null → reducer defensively treats as "not advisory".
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-unknown", "tenant-unknown");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                30, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(15),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            // EspConfigDetected was never posted → EspAllowContinueAnyway stays null.
            Assert.Null(state.ScenarioObservations.EspAllowContinueAnyway);
            Assert.NotNull(state.AccountSetupEnteredUtc);

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string> { ["failedSubcategory"] = "Certificates" }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Null(step.NewState.EspAdvisoryFailureRecordedUtc);
            SingleTimelineEffect(step, "enrollment_failed");
        }

        // ============================================ typed-payload merge (failed path) ==

        [Fact]
        public void EspTerminalFailure_FailedPath_WithContinueAnywayHints_TypedPayloadContainsAllKeys()
        {
            // ContinueAnyway=true + AccountSetupEnteredUtc=null → Advisory-Guard does NOT trigger,
            // Failed path runs. But the ContinueAnyway hint keys ARE present on the parameter bag
            // and must be merged into the typed payload (Codex review #2 / #18 — the existing
            // enrollment_failed event lost these keys in wire-Data before this fix).
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-merge", "tenant-merge");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "true",
                    [SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = "30",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;

            Assert.Null(state.AccountSetupEnteredUtc);

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(15),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
                    ["errorCode"] = "0xdeadbeef",
                    ["failedSubcategory"] = "Certificates",
                    ["category"] = "DeviceSetup",
                }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            var effect = SingleTimelineEffect(step, "enrollment_failed");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            // Audit-trail anchors (DecisionAuditTrailBuilder output).
            Assert.Equal("DecisionEngine", payload["decisionSource"]);
            Assert.Equal(nameof(DecisionSignalKind.EspTerminalFailure), payload["trigger"]);

            // Failure-context whitelist all present in typedPayload.
            Assert.Equal("Provisioning_DeviceSetup_Certificates_Failed", payload["failureType"]);
            Assert.Equal("0xdeadbeef", payload["errorCode"]);
            Assert.Equal("Certificates", payload["failedSubcategory"]);
            Assert.Equal("DeviceSetup", payload["category"]);
            Assert.Equal("30", payload["espSyncFailureTimeoutMinutes"]);
            Assert.Equal("true", payload["espAllowContinueAnyway"]);
            Assert.Equal("true", payload["mayHaveContinuedAnyway"]);
            Assert.True(payload.ContainsKey("continueAnywayHint"));

            // No advisoryReason in Failed-path output.
            Assert.False(payload.ContainsKey("advisoryReason"));
            // Emitter-metadata not duplicated.
            Assert.False(payload.ContainsKey("eventType"));
        }

        [Fact]
        public void EspTerminalFailure_FailedPath_WithoutContinueAnyway_TypedPayloadOmitsContinueAnywayKeys()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-merge-neg", "tenant-merge-neg");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "false",
                })).NewState;

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(15),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
                    ["errorCode"] = "0xdeadbeef",
                }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            var effect = SingleTimelineEffect(step, "enrollment_failed");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            // Failure-context present.
            Assert.Equal("Provisioning_DeviceSetup_Certificates_Failed", payload["failureType"]);
            Assert.Equal("0xdeadbeef", payload["errorCode"]);

            // espAllowContinueAnyway=false is recorded but no hint/mayHave is set.
            Assert.Equal("false", payload["espAllowContinueAnyway"]);
            Assert.False(payload.ContainsKey("mayHaveContinuedAnyway"));
            Assert.False(payload.ContainsKey("continueAnywayHint"));
            Assert.False(payload.ContainsKey("advisoryReason"));
        }
    }
}
