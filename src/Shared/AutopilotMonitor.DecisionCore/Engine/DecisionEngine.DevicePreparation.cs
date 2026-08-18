using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // Device Preparation (WDP) — completion backstop (afee7ae0, 2026-08-18).
    //
    // WDP has no ESP, so every ESP-derived resolution deadline is structurally unarmable
    // there: HelloSafety arms only on EspExiting / AccountSetupProvisioningComplete / arm C,
    // AdvisoryCompletion only on an EspTerminalFailure or a guard-blocked post-AccountSetup
    // esp_exiting — none of which exist on Device Preparation. A WDP session whose Hello
    // never resolves therefore used to park until the agent max-lifetime watchdog with no
    // verdict. The engine otherwise deliberately rides the Classic rails for WDP (the
    // Hello + Desktop conjunction and arm D of ShouldTransitionToAwaitingHello); this
    // partial adds only the WDP-specific bounded wait.
    public sealed partial class DecisionEngine
    {
        /// <summary>
        /// Resolution window from real-user desktop arrival on a Device Preparation session.
        /// Deliberately the same order of magnitude as <c>AdvisoryCompletion</c> (30 min),
        /// not <c>HelloSafety</c> (300 s): the user may legitimately still be walking
        /// through a configured Hello wizard, and the tracker's own Hello completion
        /// timers normally resolve long before this fires — it is the last net, not the
        /// expected path.
        /// </summary>
        private static readonly TimeSpan s_devicePrepCompletionWindow = TimeSpan.FromMinutes(30);

        /// <summary>True when <see cref="DeadlineNames.DevicePrepCompletion"/> is armed in <paramref name="state"/>.</summary>
        private static bool HasDevicePrepCompletionDeadline(DecisionState state)
        {
            foreach (var d in state.Deadlines)
            {
                if (d.Name == DeadlineNames.DevicePrepCompletion) return true;
            }
            return false;
        }

        /// <summary>
        /// Build the <see cref="DeadlineNames.DevicePrepCompletion"/> backstop deadline.
        /// Replay-safe via <see cref="EffectiveDeadlineBase"/> (floored at AgentBootUtc).
        /// </summary>
        internal static ActiveDeadline BuildDevicePrepCompletionDeadline(DecisionState state, DecisionSignal signal) =>
            new ActiveDeadline(
                name: DeadlineNames.DevicePrepCompletion,
                dueAtUtc: EffectiveDeadlineBase(state, signal).Add(s_devicePrepCompletionWindow),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.DevicePrepCompletion,
                });

        /// <summary>
        /// <see cref="DeadlineNames.DevicePrepCompletion"/> fired: the WDP session reached
        /// the real-user desktop but Hello never resolved within the window. Mirror of
        /// <see cref="HandleHelloSafetyDeadlineFired"/>: record the synthetic
        /// <see cref="SyntheticHelloOutcomeTimeout"/> (unless a real resolution raced the
        /// timer) and complete through Finalizing. Desktop is present by construction of
        /// the arming site (<c>HandleDesktopArrivedV1</c>); the AwaitingDesktop fallback
        /// only keeps the handler total. A timer surviving into a terminal stage is
        /// swallowed by the post-terminal guard in <see cref="Dispatch"/> — no explicit
        /// cancel wiring at the completion sites is needed for correctness.
        /// </summary>
        private DecisionStep HandleDevicePrepCompletionDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.DevicePrepCompletion);

            if (state.HelloResolvedUtc == null)
            {
                builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
                builder.HelloOutcome = new SignalFact<string>(SyntheticHelloOutcomeTimeout, signal.SessionSignalOrdinal);
            }

            if (state.DesktopArrivedUtc != null)
            {
                return CompleteThroughFinalizingOrDefer(
                    state: state,
                    signal: signal,
                    preparedBuilder: builder,
                    nextStepIndex: nextStep,
                    trigger: $"DeadlineFired:{DeadlineNames.DevicePrepCompletion}");
            }

            builder.WithStage(SessionStage.AwaitingDesktop);

            var waitingEffect = BuildCompletionWaitingEffect(
                state, builder, signal, trigger: $"DeadlineFired:{DeadlineNames.DevicePrepCompletion}");

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.AwaitingDesktop,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.DevicePrepCompletion}");

            return new DecisionStep(
                newState,
                transition,
                waitingEffect != null ? new[] { waitingEffect } : Array.Empty<DecisionEffect>());
        }
    }
}
