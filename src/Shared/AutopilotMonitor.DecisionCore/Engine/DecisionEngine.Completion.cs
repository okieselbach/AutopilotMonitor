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
    // 330f73f3). This partial centralizes the gate SET and the deferral shape so adding a new
    // completion precondition (e.g. WDP v2 device-association settle) is one entry in
    // s_completionGates rather than an edit at every completion site.
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
        /// scenario registers one gate here and every completion site that routes through
        /// <see cref="CompleteThroughFinalizingOrDefer"/> inherits it — no per-site edits.
        /// RealmJoin is the only gate today (the SelfDeploying deadline path additionally owns a
        /// gate-specific deferral; see <c>HandleDeviceOnlyEspDetectionDeadlineFired</c>).
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
