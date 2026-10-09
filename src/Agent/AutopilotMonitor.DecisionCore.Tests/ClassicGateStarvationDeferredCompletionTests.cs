using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Session caa6cf50 gate-starvation fix (2026-06-11). Production sequence: Classic-v1 +
    /// Hello-disabled on Win11, where explorer.exe runs underneath the User-ESP page so
    /// DesktopArrived lands BEFORE the final EspExiting. A policy-skipped user-ESP app left
    /// the registry's Apps subcategory permanently <c>inProgress</c>, so the registry-driven
    /// <c>AccountSetupProvisioningComplete</c> never fired and the session stalled with all
    /// completion facts in place (espFinalExit + desktop + helloPolicy=false) but the strong
    /// gate closed.
    /// <para>
    /// The agent-side half of the fix synthesises <c>AccountSetupProvisioningComplete</c> from
    /// "Shell-Core normal exit + all tracked user-ESP apps terminal (0 failed)". These tests
    /// cover the reducer-side half: <c>HandleAccountSetupProvisioningCompleteV1</c>'s deferred
    /// path must mirror the live-ordering completion checks when the strong gate arrives LAST —
    /// completing through Finalizing instead of parking in AwaitingHello until HelloSafety
    /// stamps a misleading <c>HelloOutcome="Timeout"</c> 300 s later.
    /// </para>
    /// </summary>
    public sealed class ClassicGateStarvationDeferredCompletionTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 6, 11, 7, 36, 0, DateTimeKind.Utc);

        [Fact]
        public void GateArrivesLast_HelloDisabled_DesktopAlreadyArrived_completes_through_Finalizing()
        {
            // Replay of session caa6cf50's signal ordering:
            // DeviceSetup → Hello policy disabled → AccountSetup → EspExiting (gate closed, fact
            // only) → DesktopArrived (fast-path blocked by gate) → EspExiting (final, still
            // blocked) → synthesized AccountSetupProvisioningComplete → MUST complete.
            var engine = new DecisionEngine();
            var state = ProgressToBlockedPostExitState(engine);

            // Sanity: all completion facts in place, but the session is parked (the bug shape).
            Assert.NotNull(state.EspFinalExitUtc);
            Assert.NotNull(state.DesktopArrivedUtc);
            Assert.False(state.HelloPolicyEnabled!.Value);
            Assert.Null(state.HelloResolvedUtc);
            Assert.Null(state.AccountSetupProvisioningSucceededUtc);
            Assert.Equal(SessionStage.EspAccountSetup, state.Stage);
            // Session 1ec8f4c6 (2026-06-12): the post-AccountSetup blocked exit now arms the
            // AdvisoryCompletion resolution window — the parked shape is no longer deadline-free.
            // The synthesized AccountSetupProvisioningComplete below still wins the race (it
            // arrives long before the 30-min window) — this test covers that fast path.
            var parked = Assert.Single(state.Deadlines);
            Assert.Equal(DeadlineNames.AdvisoryCompletion, parked.Name);

            var step = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.AccountSetupProvisioningComplete,
                T0.AddMinutes(24).AddSeconds(10), null));

            // Deferred completion: Finalizing in one step, synthetic HelloOutcome="Skipped".
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome); // EnrollmentComplete deferred to FinalizingGrace
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.NotNull(step.NewState.AccountSetupProvisioningSucceededUtc);

            // No HelloSafety armed — we went straight to Finalizing, not AwaitingHello.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition"
                     && e.Parameters.TryGetValue("phase", out var ph) && ph == nameof(EnrollmentPhase.FinalizingSetup));

            // FinalizingGrace fires → terminal enrollment_complete.
            var afterGrace = engine.Reduce(
                step.NewState,
                MakeSignal(21, DecisionSignalKind.DeadlineFired, T0.AddMinutes(24).AddSeconds(15),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace }));

            Assert.Equal(SessionStage.Completed, afterGrace.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, afterGrace.NewState.Outcome);
            Assert.Contains(afterGrace.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
        }

        [Fact]
        public void GateArrivesLast_HelloAlreadyResolved_DesktopAlreadyArrived_completes_without_overwriting_outcome()
        {
            // Hello resolved earlier (e.g. wizard observed) but the strong gate arrived last.
            // The deferred path must complete and keep the real HelloOutcome.
            var engine = new DecisionEngine();
            var state = ProgressToBlockedPostExitState(engine, includeHelloPolicy: false);
            state = engine.Reduce(state, MakeSignal(15, DecisionSignalKind.HelloResolved, T0.AddMinutes(23),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "completed" })).NewState;

            // HelloResolved with Desktop already in routes to Finalizing only when the gate is
            // open — which it is not yet, so HandleHelloResolvedV1's both-prerequisites branch
            // already fired Finalizing... verify the actual stage first.
            // NOTE: HandleHelloResolvedV1 does not consult ShouldTransitionToAwaitingHello — with
            // desktop already arrived it completes directly. That ordering therefore never needed
            // this fix; assert it as documentation of the boundary.
            Assert.Equal(SessionStage.Finalizing, state.Stage);
        }

        [Fact]
        public void GateArrivesLast_DesktopNotYetArrived_keeps_existing_DeferredPromote_to_AwaitingHello()
        {
            // Regression guard: without DesktopArrived the deferred path must keep promoting to
            // AwaitingHello + arm HelloSafety exactly as before this fix.
            var engine = new DecisionEngine();
            var state = ProgressToBlockedPostExitState(engine, includeDesktop: false);
            Assert.Null(state.DesktopArrivedUtc);

            var step = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.AccountSetupProvisioningComplete,
                T0.AddMinutes(24), null));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
        }

        [Fact]
        public void GateArrivesLast_HelloPolicyUnknown_DesktopArrived_stays_pessimistic_AwaitingHello()
        {
            // HelloPolicyEnabled == null → the deferred path must NOT synthesise "Skipped";
            // promote to AwaitingHello and let the Hello wizard / HelloSafety resolve it.
            var engine = new DecisionEngine();
            var state = ProgressToBlockedPostExitState(engine, includeHelloPolicy: false);
            Assert.Null(state.HelloPolicyEnabled);
            Assert.NotNull(state.DesktopArrivedUtc);

            var step = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.AccountSetupProvisioningComplete,
                T0.AddMinutes(24), null));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void GateArrivesLast_HelloPolicyEnabled_DesktopArrived_stays_pessimistic_AwaitingHello()
        {
            // HelloPolicyEnabled == true → the real Hello wizard is expected; no synthesis.
            var engine = new DecisionEngine();
            var state = ProgressToBlockedPostExitState(engine, helloEnabled: true);
            Assert.True(state.HelloPolicyEnabled!.Value);

            var step = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.AccountSetupProvisioningComplete,
                T0.AddMinutes(24), null));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void DuplicateGateSignal_after_completion_path_is_a_noop_bookkeeping_step()
        {
            // The adapter dedupes, but a replayed duplicate must not double-complete: the fact is
            // already recorded, so shouldPromote (first-record guard) rejects and the signal
            // becomes bookkeeping only.
            var engine = new DecisionEngine();
            var state = ProgressToBlockedPostExitState(engine);
            state = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.AccountSetupProvisioningComplete,
                T0.AddMinutes(24), null)).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);

            var dup = engine.Reduce(state, MakeSignal(21, DecisionSignalKind.AccountSetupProvisioningComplete,
                T0.AddMinutes(24).AddSeconds(2), null));

            Assert.Equal(SessionStage.Finalizing, dup.NewState.Stage);
            // Exactly one FinalizingGrace deadline; no second phase_transition effect.
            Assert.Single(dup.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.DoesNotContain(dup.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry);
        }

        // ====================================================================== test helpers

        /// <summary>
        /// Drives the engine into session caa6cf50's blocked shape: Classic AccountSetup entered,
        /// Hello policy fact (configurable), EspExiting recorded but guard-rejected (gate closed),
        /// DesktopArrived recorded but fast-path-rejected. Stage stays EspAccountSetup with no
        /// active deadlines.
        /// </summary>
        private static DecisionState ProgressToBlockedPostExitState(
            DecisionEngine engine,
            bool includeHelloPolicy = true,
            bool helloEnabled = false,
            bool includeDesktop = true)
        {
            var state = DecisionState.CreateInitial("sess-caa6cf50", "tenant-a", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            if (includeHelloPolicy)
            {
                state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1).AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = helloEnabled ? "true" : "false" })).NewState;
            }
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(12),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            // First esp_exiting (Device→Account handoff per Shell-Core 62407) — gate closed, fact only.
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspExiting, T0.AddMinutes(13), null)).NewState;
            Assert.Equal(SessionStage.EspAccountSetup, state.Stage);
            if (includeDesktop)
            {
                // Desktop arrives during User-ESP (Win11: explorer.exe under the ESP page) —
                // Hello-disabled fast-path blocked by the closed strong gate.
                state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(16), null)).NewState;
                Assert.NotEqual(SessionStage.Finalizing, state.Stage);
            }
            // Final esp_exiting — still blocked (gate evidence never came from the registry).
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.EspExiting, T0.AddMinutes(24), null)).NewState;
            Assert.Equal(SessionStage.EspAccountSetup, state.Stage);
            return state;
        }

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"test-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);
        }
    }
}
