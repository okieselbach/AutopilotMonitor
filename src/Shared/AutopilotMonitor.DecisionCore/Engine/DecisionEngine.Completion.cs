using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // Completion-gate weaving (ARCH-F1). The cross-cutting "may the session complete now?"
    // AND-gate used to be hand-inlined at every terminal site across the Classic / Shared /
    // SelfDeploying / RealmJoin engine partials, each with its own near-identical
    // RealmJoinGateOpen check + ":RealmJoinGateClosed" defer block. That duplication was the
    // origin of the premature/duplicate enrollment_complete regressions (8b8d611d, 08c99638,
    // 330f73f3). This partial centralizes the gate SET and the deferral shape for the
    // completion-ATTEMPT sites — the four Classic/Shared handlers that route a "both
    // prerequisites in" decision through CompleteThroughFinalizingOrDefer. Those sites inherit a
    // new gate purely by it appearing in s_completionGates.
    //
    // Two paths intentionally do NOT consult the collection (single-gate-correct today; a second
    // gate must wire them explicitly — they are the gate's release/origin, not attempt sites):
    //   * the RealmJoin release path (CompleteIfDeferredOrBookkeep, DecisionEngine.RealmJoin.cs)
    //     completes directly because it evaluates the PRE-resolution state where the gate it just
    //     opened still reads closed; a second gate that must re-block here needs a post-state
    //     re-check;
    //   * the SelfDeploying deadline path (HandleDeviceOnlyEspDetectionDeadlineFired) owns a
    //     gate-specific deferral (SelfDeployingDeferredCompletion on RealmJoinFacts, released by
    //     the RealmJoin handlers) and so checks RealmJoinGateOpen directly.
    public sealed partial class DecisionEngine
    {
        /// <summary>
        /// A cross-cutting completion precondition. Completion is deferred while any gate is
        /// closed; the deferring transition is tagged <c>:&lt;Name&gt;Closed</c> until a later
        /// signal re-opens the gate and a completion-release handler re-attempts.
        /// </summary>
        private readonly struct CompletionGate
        {
            public CompletionGate(string name, Func<DecisionState, bool> isOpen)
            {
                Name = name;
                IsOpen = isOpen;
            }

            /// <summary>Stable gate identifier; forms the <c>:&lt;Name&gt;Closed</c> trigger suffix.</summary>
            public string Name { get; }

            /// <summary>Returns <c>true</c> when this precondition no longer blocks completion.</summary>
            public Func<DecisionState, bool> IsOpen { get; }
        }

        /// <summary>
        /// The ordered set of completion preconditions. <b>Append-only</b>: a new enrollment
        /// scenario registers one gate here and every completion-ATTEMPT site that routes through
        /// <see cref="CompleteThroughFinalizingOrDefer"/> inherits it with no per-site edits.
        /// RealmJoin is the only gate today. Note this collection does NOT cover the RealmJoin
        /// release path or the SelfDeploying deadline path — both are gate-specific by design
        /// (see the class-level remarks); a second gate that must block there needs explicit
        /// wiring, not just an entry here.
        /// </summary>
        private static readonly CompletionGate[] s_completionGates =
        {
            // An active RealmJoin deployment (detected, not yet resolved / timed-out) blocks
            // completion until RealmJoinResolved (phase 110) or the 60-min hard timeout fires.
            new CompletionGate("RealmJoinGate", RealmJoinGateOpen),
        };

        /// <summary>
        /// Returns the <see cref="CompletionGate.Name"/> of the first closed completion gate, or
        /// <c>null</c> when every gate is open (completion may proceed). Centralizes the
        /// cross-cutting AND-gate (ARCH-F1).
        /// </summary>
        private static string? FirstClosedCompletionGate(DecisionState state)
        {
            foreach (var gate in s_completionGates)
            {
                if (!gate.IsOpen(state)) return gate.Name;
            }
            return null;
        }

        /// <summary>
        /// Shared completion router for every "both prerequisites in → finish via Finalizing"
        /// site (Classic Hello/Desktop handlers, the Hello-disabled fast-path, the Hello-safety
        /// timeout, and the RealmJoin release path). When all completion gates are open this
        /// routes through <see cref="TransitionToFinalizing"/>; when a gate is closed it emits
        /// the canonical defer step — current stage unchanged, trigger tagged
        /// <c>:&lt;Gate&gt;Closed</c>, the same <paramref name="leadingEffects"/> preserved, no
        /// deadline armed — so a later gate-release handler re-attempts completion.
        /// <para>
        /// The caller has already populated <paramref name="preparedBuilder"/> with its
        /// signal-specific facts (HelloResolvedUtc / DesktopArrivedUtc / Hello-safety cancel).
        /// </para>
        /// </summary>
        private DecisionStep CompleteThroughFinalizingOrDefer(
            DecisionState state,
            DecisionSignal signal,
            DecisionStateBuilder preparedBuilder,
            int nextStepIndex,
            string trigger,
            IReadOnlyList<DecisionEffect>? leadingEffects = null)
        {
            var closedGate = FirstClosedCompletionGate(state);
            if (closedGate == null)
            {
                return TransitionToFinalizing(
                    state: state,
                    signal: signal,
                    preparedBuilder: preparedBuilder,
                    nextStepIndex: nextStepIndex,
                    trigger: trigger,
                    extraLeadingEffects: leadingEffects);
            }

            // Gate closed: defer Finalizing. Stay in the current stage; the gate's resolved/
            // timeout handler routes back through completion once it re-opens.
            //
            // Liveness plan PR2: surface what completion is waiting on (state-change-only —
            // the fingerprint fact dedupes repeats of the same missing-set).
            var deferTrigger = trigger + ":" + closedGate + "Closed";
            var waitingEffect = BuildCompletionWaitingEffect(state, preparedBuilder, signal, deferTrigger);

            var deferredState = preparedBuilder.Build();
            var deferredTransition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStepIndex,
                trigger: deferTrigger);
            return new DecisionStep(
                deferredState,
                deferredTransition,
                AppendEffect(MaterializeEffects(leadingEffects), waitingEffect));
        }

        // ====================================================== completion_waiting (PR2)

        /// <summary>Stable literals for <c>completion_waiting</c>'s <c>missingPrerequisites</c> data field.</summary>
        internal static class CompletionPrerequisites
        {
            public const string AccountSetupProvisioningComplete = "account_setup_provisioning_complete";
            public const string HelloResolution = "hello_resolution";
            public const string DesktopArrival = "desktop_arrival";
            public const string RealmJoinResolution = "realmjoin_resolution";
        }

        // ====================================================== Hello-satisfied predicate
        // Session 772fe502 (2026-07-13): a flip-flopping user-scoped WHfB CSP was read once
        // as disabled, the engine synthesized HelloOutcome="Skipped" and completed while the
        // Hello wizard — started 230 ms earlier — was still on screen. Every "may policy-
        // disabled stand in for a Hello resolution?" decision now routes through the two
        // predicates below so the wizard-started fact vetoes the shortcut in one place.

        /// <summary>
        /// Engine-synthesized Hello outcome written by the policy-disabled completion sites.
        /// Deliberately distinct from the tracker vocabulary (all lowercase: "completed",
        /// "skipped", "not_configured", "timeout", "wizard_not_started") so
        /// <see cref="HasEngineSynthesizedHelloSkip"/> can discriminate a synthetic skip from
        /// a real tracker-posted resolution by exact-case comparison.
        /// </summary>
        internal const string SyntheticHelloOutcomeSkipped = "Skipped";

        /// <summary>
        /// Engine-synthesized Hello outcome written by the HelloSafety timeout handler.
        /// Same casing contract as <see cref="SyntheticHelloOutcomeSkipped"/>.
        /// </summary>
        internal const string SyntheticHelloOutcomeTimeout = "Timeout";

        /// <summary>
        /// True when an explicitly-disabled Hello policy may stand in for a Hello resolution:
        /// the policy reader said disabled AND no Hello wizard launch has been observed. Once
        /// Shell-Core 62404 (CXID AADHello/NGC) is on record the wizard is demonstrably
        /// running — the policy read was stale or flip-flopped — and only a real
        /// <c>HelloResolved</c> (or the HelloSafety timeout) may satisfy the Hello gate.
        /// Fact-based signature so both state-based guards and the builder-based
        /// <see cref="BuildCompletionWaitingEffect"/> share it.
        /// </summary>
        private static bool HelloPolicyDisabledWithoutWizard(
            SignalFact<bool>? helloPolicyEnabled,
            SignalFact<DateTime>? helloWizardStartedUtc) =>
            helloPolicyEnabled?.Value == false && helloWizardStartedUtc == null;

        /// <summary>
        /// The completion-side Hello gate: an actual resolution, or the policy-disabled
        /// stand-in (vetoed by an observed wizard start — see
        /// <see cref="HelloPolicyDisabledWithoutWizard"/>).
        /// </summary>
        private static bool HelloSatisfiedForCompletion(DecisionState state) =>
            state.HelloResolvedUtc != null
            || HelloPolicyDisabledWithoutWizard(state.HelloPolicyEnabled, state.HelloWizardStartedUtc);

        /// <summary>
        /// True when the recorded Hello resolution is the engine-synthesized policy-disabled
        /// skip (<see cref="SyntheticHelloOutcomeSkipped"/>, exact-case) — the only Hello fact
        /// the <c>HandleHelloWizardStartedV1</c> cure is allowed to retract. Tracker-posted
        /// outcomes (lowercase vocabulary) and the synthetic HelloSafety
        /// <see cref="SyntheticHelloOutcomeTimeout"/> are never retracted.
        /// </summary>
        private static bool HasEngineSynthesizedHelloSkip(DecisionState state) =>
            state.HelloResolvedUtc != null
            && string.Equals(state.HelloOutcome?.Value, SyntheticHelloOutcomeSkipped, StringComparison.Ordinal);

        /// <summary>
        /// Record the engine-synthesized policy-disabled Hello skip on the builder. Single
        /// synthesis point for the <see cref="SyntheticHelloOutcomeSkipped"/> literal so the
        /// discriminator contract lives in one place.
        /// </summary>
        private static void SynthesizeHelloSkipped(DecisionStateBuilder builder, DecisionSignal signal)
        {
            builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            builder.HelloOutcome = new SignalFact<string>(SyntheticHelloOutcomeSkipped, signal.SessionSignalOrdinal);
        }

        /// <summary>
        /// Compute the ordered list of completion prerequisites the engine is still waiting on
        /// for <paramref name="state"/>. Liveness plan PR2 — feeds the <c>completion_waiting</c>
        /// event's <c>missingPrerequisites</c> field and the dedupe fingerprint.
        /// </summary>
        internal static List<string> BuildMissingCompletionPrerequisites(DecisionState state) =>
            BuildMissingCompletionPrerequisitesCore(
                accountSetupProvisioned: state.AccountSetupProvisioningSucceededUtc != null,
                skipUserEsp: state.ScenarioObservations.SkipUserEsp?.Value == true,
                helloResolved: state.HelloResolvedUtc != null,
                helloPolicyDisabled: HelloPolicyDisabledWithoutWizard(state.HelloPolicyEnabled, state.HelloWizardStartedUtc),
                desktopArrived: state.DesktopArrivedUtc != null,
                realmJoinGateOpen: RealmJoinGateOpen(state),
                postEspUserSessionEvidence: HasPostEspUserSessionEvidence(
                    state.AccountSetupEnteredUtc,
                    state.EspFinalExitUtc,
                    state.ImeUserSessionCompletedUtc,
                    state.DesktopArrivedUtc));

        /// <summary>
        /// Arm C of <c>ShouldTransitionToAwaitingHello</c> (session a4537c36) restated over raw
        /// facts so the builder-based <see cref="BuildCompletionWaitingEffect"/> can evaluate
        /// in-flight values: AccountSetup entered + normal ESP final exit + genuine
        /// (at-or-after-anchor) IME user-session completion + real-user desktop. When this
        /// holds, <c>account_setup_provisioning_complete</c> is no longer reported missing —
        /// the registry gate is unsatisfiable by construction in exactly this shape.
        /// </summary>
        private static bool HasPostEspUserSessionEvidence(
            SignalFact<DateTime>? accountSetupEntered,
            SignalFact<DateTime>? espFinalExit,
            SignalFact<DateTime>? imeUserSessionCompleted,
            SignalFact<DateTime>? desktopArrived) =>
            accountSetupEntered != null
            && espFinalExit != null
            // Ingest-ordinal comparison, not timestamps — see IsPostAccountSetupFinalExit
            // (backdated CMTrace exits; sequence is the canonical order).
            && espFinalExit.SourceSignalOrdinal > accountSetupEntered.SourceSignalOrdinal
            && imeUserSessionCompleted != null
            && imeUserSessionCompleted.Value >= accountSetupEntered.Value
            && desktopArrived != null;

        private static List<string> BuildMissingCompletionPrerequisitesCore(
            bool accountSetupProvisioned,
            bool skipUserEsp,
            bool helloResolved,
            bool helloPolicyDisabled,
            bool desktopArrived,
            bool realmJoinGateOpen,
            bool postEspUserSessionEvidence = false)
        {
            var missing = new List<string>(4);
            if (!accountSetupProvisioned && !skipUserEsp && !postEspUserSessionEvidence)
                missing.Add(CompletionPrerequisites.AccountSetupProvisioningComplete);
            if (!helloResolved && !helloPolicyDisabled)
                missing.Add(CompletionPrerequisites.HelloResolution);
            if (!desktopArrived)
                missing.Add(CompletionPrerequisites.DesktopArrival);
            if (!realmJoinGateOpen)
                missing.Add(CompletionPrerequisites.RealmJoinResolution);
            return missing;
        }

        /// <summary>
        /// Build the <c>completion_waiting</c> timeline effect for a blocked / deferred
        /// completion attempt, or <c>null</c> when nothing should be emitted. Liveness plan PR2.
        /// <para>
        /// The missing-prerequisites set is computed from the <paramref name="builder"/>'s
        /// in-flight facts (so facts the current handler just recorded — e.g. a freshly resolved
        /// Hello — are not listed as missing). State-change-only by construction: when the
        /// comma-joined set equals <see cref="DecisionState.CompletionWaitingFingerprint"/> on
        /// the <paramref name="before"/> state, no event is emitted. Otherwise the fingerprint
        /// fact is advanced on the builder and the effect is returned. An empty missing-set
        /// (e.g. a duplicate signal on an already-satisfied state) emits nothing.
        /// </para>
        /// </summary>
        private static DecisionEffect? BuildCompletionWaitingEffect(
            DecisionState before,
            DecisionStateBuilder builder,
            DecisionSignal signal,
            string trigger,
            IReadOnlyDictionary<string, string>? extraData = null)
        {
            var missing = BuildMissingCompletionPrerequisitesCore(
                accountSetupProvisioned: builder.AccountSetupProvisioningSucceededUtc != null,
                skipUserEsp: builder.ScenarioObservations.SkipUserEsp?.Value == true,
                helloResolved: builder.HelloResolvedUtc != null,
                helloPolicyDisabled: HelloPolicyDisabledWithoutWizard(builder.HelloPolicyEnabled, builder.HelloWizardStartedUtc),
                desktopArrived: builder.DesktopArrivedUtc != null,
                realmJoinGateOpen: RealmJoinGateOpen(builder.RealmJoinFacts),
                postEspUserSessionEvidence: HasPostEspUserSessionEvidence(
                    builder.AccountSetupEnteredUtc,
                    builder.EspFinalExitUtc,
                    builder.ImeUserSessionCompletedUtc,
                    builder.DesktopArrivedUtc));
            if (missing.Count == 0) return null;

            var fingerprint = string.Join(",", missing);
            if (before.CompletionWaitingFingerprint?.Value == fingerprint) return null;

            builder.CompletionWaitingFingerprint =
                new SignalFact<string>(fingerprint, signal.SessionSignalOrdinal);

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["eventType"] = SharedConstants.EventTypes.CompletionWaiting,
                ["source"] = "DecisionEngine",
                ["severity"] = "Info",
                ["immediateUpload"] = "false",
                ["message"] = $"Completion is waiting on: {fingerprint}",
                ["missingPrerequisites"] = fingerprint,
                ["trigger"] = trigger,
                ["stage"] = builder.Stage.ToString(),
            };

            if (builder.Deadlines.Count > 0)
            {
                var names = new string[builder.Deadlines.Count];
                for (var i = 0; i < builder.Deadlines.Count; i++)
                {
                    var d = builder.Deadlines[i];
                    names[i] = $"{d.Name}={d.DueAtUtc:o}";
                }
                parameters["armedDeadlines"] = string.Join(",", names);
            }

            if (extraData != null)
            {
                foreach (var kv in extraData)
                {
                    if (!parameters.ContainsKey(kv.Key)) parameters[kv.Key] = kv.Value;
                }
            }

            return new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry, parameters: parameters);
        }

        /// <summary>Append an optional effect to a materialized effect array (no-op when null).</summary>
        private static DecisionEffect[] AppendEffect(DecisionEffect[] effects, DecisionEffect? extra)
        {
            if (extra == null) return effects;
            var combined = new DecisionEffect[effects.Length + 1];
            Array.Copy(effects, combined, effects.Length);
            combined[effects.Length] = extra;
            return combined;
        }

        /// <summary>
        /// Materialize an optional effect list into the array shape the <see cref="DecisionStep"/>
        /// defer path expects. Returns the shared empty array when there is nothing to emit, and
        /// passes a backing array through untouched to avoid a copy.
        /// </summary>
        private static DecisionEffect[] MaterializeEffects(IReadOnlyList<DecisionEffect>? effects)
        {
            if (effects == null || effects.Count == 0) return Array.Empty<DecisionEffect>();
            if (effects is DecisionEffect[] arr) return arr;
            var copy = new DecisionEffect[effects.Count];
            for (var i = 0; i < effects.Count; i++) copy[i] = effects[i];
            return copy;
        }
    }
}
