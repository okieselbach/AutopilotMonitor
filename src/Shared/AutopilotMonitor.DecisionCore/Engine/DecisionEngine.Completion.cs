using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

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
            var deferredState = preparedBuilder.Build();
            var deferredTransition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStepIndex,
                trigger: trigger + ":" + closedGate + "Closed");
            return new DecisionStep(deferredState, deferredTransition, MaterializeEffects(leadingEffects));
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
