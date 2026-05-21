using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Coverage for <see cref="DecisionAuditTrailBuilder"/> and the DecisionEngine emission
    /// sites that previously published terminal / state-changing timeline events with empty
    /// or near-empty <c>EnrollmentEvent.Data</c>: <c>whiteglove_complete</c>,
    /// <c>whiteglove_resumed</c>, <c>enrollment_failed</c> (EspTerminalFailure /
    /// EffectInfrastructureFailure). Each test exercises the reducer's typed-payload
    /// contract end-to-end so a regression to "empty Data on the wire" is caught at the
    /// engine boundary instead of in a UI smoke test.
    /// </summary>
    public sealed class DecisionAuditTrailTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 4, 30, 8, 0, 0, DateTimeKind.Utc);

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

        // ============================================================ builder unit tests

        [Fact]
        public void Build_emits_mandatory_anchor_fields_for_minimal_state()
        {
            var state = DecisionState.CreateInitial("s", "t").ToBuilder()
                .WithStepIndex(7)
                .WithLastAppliedSignalOrdinal(42)
                .Build();

            var data = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.SessionStarted,
                trigger: "Test:Trigger");

            Assert.Equal("DecisionEngine", data["decisionSource"]);
            Assert.Equal("Test:Trigger", data["trigger"]);
            Assert.Equal(nameof(SessionStage.SessionStarted), data["sessionStage"]);
            Assert.Equal(7, data["stepIndex"]);
            Assert.Equal(42L, data["signalOrdinal"]);
            Assert.IsType<List<string>>(data["signalsSeen"]);
            Assert.IsType<Dictionary<string, object>>(data["signalEvidence"]);
            // Schema-Drift Sync (2026-05-04): signalTimestamps is now Dictionary<string, string>
            // — values are pure ISO-8601 strings, sourced from DecisionStateSignalCensus shared
            // with the V2 agent's FinalStatusBuilder.
            Assert.IsType<Dictionary<string, string>>(data["signalTimestamps"]);
            Assert.IsType<Dictionary<string, object>>(data["scenario"]);
            Assert.False(data.ContainsKey("classifier"));
            Assert.False(data.ContainsKey("classifierInputs"));
            Assert.False(data.ContainsKey("reason"));
        }

        [Fact]
        public void Build_omits_signalOrdinal_when_state_has_no_applied_signal()
        {
            // CreateInitial leaves LastAppliedSignalOrdinal at -1 — the builder should NOT
            // forge a "signalOrdinal=-1" entry that would mislead the Inspector.
            var state = DecisionState.CreateInitial("s", "t");

            var data = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.SessionStarted,
                trigger: "Test:NoSignal");

            Assert.False(data.ContainsKey("signalOrdinal"));
        }

        [Fact]
        public void Build_includes_failureReason_only_when_provided()
        {
            var state = DecisionState.CreateInitial("s", "t");

            var withReason = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.Failed,
                trigger: "Test:Failure",
                failureReason: "ime_pattern_failure");
            Assert.Equal("ime_pattern_failure", withReason["reason"]);

            var withoutReason = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.Failed,
                trigger: "Test:Failure");
            Assert.False(withoutReason.ContainsKey("reason"));
        }

        [Fact]
        public void Build_classifier_section_only_appears_when_verdict_supplied()
        {
            var state = DecisionState.CreateInitial("s", "t");
            var verdict = new ClassifierVerdictInfo("WhiteGloveSealing", "Confirmed", 80, "shellcore_alone", "abc123");

            var data = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.WhiteGloveSealed,
                trigger: "Test:Classifier",
                classifier: verdict);

            var classifierBlock = Assert.IsType<Dictionary<string, object>>(data["classifier"]);
            Assert.Equal("WhiteGloveSealing", classifierBlock["id"]);
            Assert.Equal("Confirmed", classifierBlock["level"]);
            Assert.Equal(80, classifierBlock["score"]);
            Assert.Equal("shellcore_alone", classifierBlock["reason"]);
            Assert.Equal("abc123", classifierBlock["inputHash"]);
        }

        [Fact]
        public void Build_classifierInputs_flattens_snapshot_record_to_camelCased_dict()
        {
            var snapshot = new WhiteGloveSealingSnapshot(
                shellCoreWhiteGloveSuccessSeen: true,
                whiteGloveSealingPatternSeen: true,
                aadJoinedWithUser: false,
                desktopArrived: false,
                helloResolved: false,
                hasAccountSetupActivity: false,
                isDeviceOnlyDeploymentHypothesis: false,
                systemRebootUtc: null,
                currentEnrollmentPhase: null);

            var data = DecisionAuditTrailBuilder.Build(
                postState: DecisionState.CreateInitial("s", "t"),
                decidedStage: SessionStage.WhiteGloveSealed,
                trigger: "Test:Inputs",
                classifierInputs: snapshot);

            var inputs = Assert.IsType<Dictionary<string, object>>(data["classifierInputs"]);
            Assert.Equal(true, inputs["shellCoreWhiteGloveSuccessSeen"]);
            Assert.Equal(true, inputs["whiteGloveSealingPatternSeen"]);
            Assert.Equal(false, inputs["aadJoinedWithUser"]);
            // Null nullable properties are skipped, not emitted as null sentinels.
            Assert.False(inputs.ContainsKey("systemRebootUtc"));
            Assert.False(inputs.ContainsKey("currentEnrollmentPhase"));
        }

        [Fact]
        public void VerdictFromSignalPayload_returns_null_for_missing_keys()
        {
            Assert.Null(DecisionAuditTrailBuilder.VerdictFromSignalPayload(null));
            Assert.Null(DecisionAuditTrailBuilder.VerdictFromSignalPayload(new Dictionary<string, string>()));
            Assert.Null(DecisionAuditTrailBuilder.VerdictFromSignalPayload(
                new Dictionary<string, string> { ["classifier"] = "X" })); // no level
        }

        [Fact]
        public void VerdictFromSignalPayload_parses_full_payload()
        {
            var v = DecisionAuditTrailBuilder.VerdictFromSignalPayload(
                new Dictionary<string, string>
                {
                    ["classifier"] = "WhiteGloveSealing",
                    ["level"] = "Confirmed",
                    ["score"] = "80",
                    ["reason"] = "shellcore_alone",
                    ["inputHash"] = "abc123",
                });

            Assert.NotNull(v);
            Assert.Equal("WhiteGloveSealing", v!.Id);
            Assert.Equal("Confirmed", v.Level);
            Assert.Equal(80, v.Score);
            Assert.Equal("shellcore_alone", v.Reason);
            Assert.Equal("abc123", v.InputHash);
        }

        // ============================================================ whiteglove_complete

        [Fact]
        public void WhiteGloveComplete_carries_audit_trail_with_classifier_verdict_and_inputs()
        {
            // NOTE: this test exercises the slow-path verdict-applier (HandleClassifierVerdictIssuedV1).
            // The newer fast-path (introduced 2026-04-30, see WhiteGloveCompleteFastPath_*
            // test below) handles the typical WG Part 1 sequence and emits the
            // whiteglove_complete event with its own audit-trail trigger
            // ("WhiteGloveShellCoreSuccess:FastPath:Confirmed"). Both paths must produce a
            // structurally identical TypedPayload; the fast-path test guards that contract.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-wg", "tenant-wg");
            // Drive through SessionStarted so the StepIndex / LastAppliedSignalOrdinal are real.
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            // Observe ShellCore success — fast-path inline-classifies and seals immediately.
            // We then synthesize a ClassifierVerdictIssued signal so the slow-path verdict
            // applier ALSO runs (idempotent on already-sealed state) and we can assert on
            // its TypedPayload independently of the fast-path emission.
            var shellSignal = MakeSignal(1, DecisionSignalKind.WhiteGloveShellCoreSuccess, T0.AddMinutes(5));
            state = engine.Reduce(state, shellSignal).NewState;

            // The reducer emitted a RunClassifier effect; the harness here synthesizes the
            // resulting verdict signal directly so the WG verdict-applier path runs.
            var verdictSignal = MakeSignal(2, DecisionSignalKind.ClassifierVerdictIssued, T0.AddMinutes(5),
                new Dictionary<string, string>
                {
                    ["classifier"] = WhiteGloveSealingClassifier.ClassifierId,
                    ["level"] = "Confirmed",
                    ["score"] = "80",
                    ["reason"] = "shellcore_alone",
                    ["inputHash"] = "deadbeef",
                });

            var step = engine.Reduce(state, verdictSignal);
            Assert.Equal(SessionStage.WhiteGloveSealed, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "whiteglove_complete");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            Assert.Equal("DecisionEngine", payload["decisionSource"]);
            Assert.Equal($"ClassifierVerdictIssued:{WhiteGloveSealingClassifier.ClassifierId}:Confirmed", payload["trigger"]);
            Assert.Equal(nameof(SessionStage.WhiteGloveSealed), payload["sessionStage"]);

            var signalsSeen = Assert.IsType<List<string>>(payload["signalsSeen"]);
            Assert.Contains("shellcore_whiteglove_success", signalsSeen);

            var classifier = Assert.IsType<Dictionary<string, object>>(payload["classifier"]);
            Assert.Equal(WhiteGloveSealingClassifier.ClassifierId, classifier["id"]);
            Assert.Equal("Confirmed", classifier["level"]);
            Assert.Equal(80, classifier["score"]);
            Assert.Equal("deadbeef", classifier["inputHash"]);

            var inputs = Assert.IsType<Dictionary<string, object>>(payload["classifierInputs"]);
            Assert.Equal(true, inputs["shellCoreWhiteGloveSuccessSeen"]);
            Assert.Equal(false, inputs["aadJoinedWithUser"]);

            var scenario = Assert.IsType<Dictionary<string, object>>(payload["scenario"]);
            Assert.Equal(EnrollmentMode.WhiteGlove.ToString(), scenario["mode"]);
        }

        [Fact]
        public void WhiteGloveCompleteFastPath_attaches_full_audit_trail_with_inline_verdict()
        {
            // Codex review follow-up (2026-04-30): the fast-path in
            // HandleWhiteGloveShellCoreSuccessV1 must produce a TypedPayload byte-stable to
            // the slow-path emission. Earlier impl only set parameters[eventType] and left
            // TypedPayload empty — this test pins the contract so a regression is caught at
            // the engine boundary.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-wg-fp", "tenant-wg-fp");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var shellSignal = MakeSignal(1, DecisionSignalKind.WhiteGloveShellCoreSuccess, T0.AddMinutes(5));
            var step = engine.Reduce(state, shellSignal);

            // Fast-path → terminal stage in this single reduce call.
            Assert.Equal(SessionStage.WhiteGloveSealed, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "whiteglove_complete");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            // Mandatory anchors — same set the slow-path emits, with the fast-path trigger.
            Assert.Equal("DecisionEngine", payload["decisionSource"]);
            Assert.Equal(
                $"{nameof(DecisionSignalKind.WhiteGloveShellCoreSuccess)}:FastPath:Confirmed",
                payload["trigger"]);
            Assert.Equal(nameof(SessionStage.WhiteGloveSealed), payload["sessionStage"]);

            // Census fields — at minimum the ShellCore signal that drove the decision.
            var signalsSeen = Assert.IsType<List<string>>(payload["signalsSeen"]);
            Assert.Contains("shellcore_whiteglove_success", signalsSeen);

            // Classifier verdict reflects the inline-computed result — Score=80 (just
            // ShellCore in a clean state), Confirmed, real (non-empty) input hash so the
            // EffectRunner's anti-loop would skip a racing RunClassifier effect.
            var classifier = Assert.IsType<Dictionary<string, object>>(payload["classifier"]);
            Assert.Equal(WhiteGloveSealingClassifier.ClassifierId, classifier["id"]);
            Assert.Equal("Confirmed", classifier["level"]);
            Assert.Equal(80, classifier["score"]);
            Assert.False(string.IsNullOrEmpty((string)classifier["inputHash"]));

            // Classifier-input snapshot must be present (flattened) — this is the field a
            // reviewer or backend forensics expects when triaging an unexpected WG sealing.
            var inputs = Assert.IsType<Dictionary<string, object>>(payload["classifierInputs"]);
            Assert.Equal(true, inputs["shellCoreWhiteGloveSuccessSeen"]);
            Assert.Equal(false, inputs["aadJoinedWithUser"]);

            var scenario = Assert.IsType<Dictionary<string, object>>(payload["scenario"]);
            Assert.Equal(EnrollmentMode.WhiteGlove.ToString(), scenario["mode"]);
        }

        // (PR-B 2026-05-04: WG-Part-2 audit-trail tests removed — `whiteglove_resumed` is
        // now an InformationalEvent emitted directly by the orchestrator (PR-A) rather than
        // a reducer-emitted timeline effect, and the Part-2 classifier / completion verdict /
        // 24h safety deadline were all deleted with the rest of the V2 WG-Part-2 apparatus.)

        // ============================================================ enrollment_failed (ESP terminal)

        [Fact]
        public void EspTerminalFailure_failure_event_carries_audit_trail_with_signal_reason()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-esp", "tenant-esp");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(15),
                new Dictionary<string, string> { ["reason"] = "policy_apply_timeout" }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "enrollment_failed");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            Assert.Equal(nameof(DecisionSignalKind.EspTerminalFailure), payload["trigger"]);
            Assert.Equal("policy_apply_timeout", payload["reason"]);
        }

        [Fact]
        public void EspTerminalFailure_event_enriched_with_continueAnyway_and_timeout_facts()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-esp-2", "tenant-esp-2");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            // Record the FirstSync-derived facts via EspConfigDetected.
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(2),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    [SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = "60",
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "true",
                })).NewState;

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string> { ["reason"] = "esp_apps_timeout" }));

            var effect = SingleTimelineEffect(step, "enrollment_failed");
            Assert.NotNull(effect.Parameters);

            Assert.Equal("60", effect.Parameters!["espSyncFailureTimeoutMinutes"]);
            Assert.Equal("true", effect.Parameters!["espAllowContinueAnyway"]);
            Assert.Equal("true", effect.Parameters!["mayHaveContinuedAnyway"]);
            Assert.Contains("Continue anyway", effect.Parameters!["continueAnywayHint"]);
        }

        [Fact]
        public void EspTerminalFailure_event_omits_hint_when_continueAnyway_false()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-esp-3", "tenant-esp-3");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(2),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "false",
                })).NewState;

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string> { ["reason"] = "esp_apps_timeout" }));

            var effect = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("false", effect.Parameters!["espAllowContinueAnyway"]);
            Assert.False(effect.Parameters!.ContainsKey("mayHaveContinuedAnyway"));
            Assert.False(effect.Parameters!.ContainsKey("continueAnywayHint"));
        }

        [Fact]
        public void EspTerminalFailure_event_omits_facts_when_never_observed()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-esp-4", "tenant-esp-4");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(30),
                new Dictionary<string, string> { ["reason"] = "esp_apps_timeout" }));

            var effect = SingleTimelineEffect(step, "enrollment_failed");
            Assert.False(effect.Parameters!.ContainsKey("espSyncFailureTimeoutMinutes"));
            Assert.False(effect.Parameters!.ContainsKey("espAllowContinueAnyway"));
            Assert.False(effect.Parameters!.ContainsKey("mayHaveContinuedAnyway"));
        }

        // ============================================================ enrollment_failed (effect infra)

        [Fact]
        public void EffectInfrastructureFailure_failure_event_carries_audit_trail_with_signal_reason()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-eff", "tenant-eff");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var step = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EffectInfrastructureFailure, T0.AddMinutes(2),
                new Dictionary<string, string> { ["reason"] = "deadline_scheduler_offline" }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "enrollment_failed");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            Assert.Equal(nameof(DecisionSignalKind.EffectInfrastructureFailure), payload["trigger"]);
            Assert.Equal("deadline_scheduler_offline", payload["reason"]);
        }
    }
}
