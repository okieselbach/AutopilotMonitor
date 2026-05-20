using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Decision Engine kernel. Plan §2.5 / L.2 / L.3.
    /// <para>
    /// Dispatches on <c>(<see cref="DecisionSignalKind"/>, <see cref="DecisionSignal.KindSchemaVersion"/>)</c>
    /// to partial-class handlers. All handlers live in sibling files named
    /// <c>DecisionEngine.Classic.cs</c>, <c>DecisionEngine.SelfDeploying.cs</c>,
    /// <c>DecisionEngine.WhiteGlove.cs</c>, <c>DecisionEngine.Shared.cs</c>.
    /// </para>
    /// <para>
    /// Exception fail-safe (L.16): a handler exception never corrupts state; the kernel
    /// catches it, advances <see cref="DecisionState.StepIndex"/>, applies the signal ordinal,
    /// and emits a dead-end <see cref="DecisionTransition"/> tagged
    /// <c>DeadEndReason="reducer_exception"</c>.
    /// </para>
    /// </summary>
    public sealed partial class DecisionEngine : IDecisionEngine
    {
        private static readonly string s_reducerVersion =
            typeof(DecisionEngine).GetTypeInfo().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        public string ReducerVersion => s_reducerVersion;

        public DecisionStep Reduce(DecisionState oldState, DecisionSignal signal)
        {
            if (oldState == null) throw new ArgumentNullException(nameof(oldState));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            try
            {
                return Dispatch(oldState, signal);
            }
            catch (Exception ex)
            {
                return BuildReducerExceptionStep(oldState, signal, ex);
            }
        }

        /// <summary>
        /// Pure dispatch on <c>(Kind, SchemaVersion)</c> — no mutation here, no try/catch
        /// (outer <see cref="Reduce"/> provides the fail-safe wrapper). Handlers return fully
        /// constructed <see cref="DecisionStep"/> instances.
        /// <para>
        /// Session 8b8d611d defense-in-depth (2026-05-20): when the state already reached a
        /// classic terminal stage (<see cref="SessionStage.Completed"/> /
        /// <see cref="SessionStage.Failed"/>), reject any further signal as a bookkept dead
        /// end before it can re-enter a handler that mutates state or emits effects. The
        /// orchestrator tear-down after <c>OnDecisionTerminalStage</c> runs on a background
        /// task (off-worker dispatch in <c>EnrollmentOrchestrator.OnDecisionTerminalStage</c>),
        /// so the SignalIngress worker can still pump queued or late-fired signals through
        /// <see cref="Reduce"/> for seconds afterwards. A live timer that escaped a missing
        /// <see cref="DecisionEffectKind.CancelDeadline"/> effect (e.g. a stale HelloSafety
        /// firing post-Completed) would otherwise re-enter <c>TransitionToFinalizing</c> and
        /// emit a duplicate <c>enrollment_complete</c>. <see cref="SessionStage.WhiteGloveSealed"/>
        /// is intentionally NOT guarded here — Part-2 resume runs as a fresh decision state
        /// after archive-and-reset, so no signal ever reaches the engine with that stage in
        /// a position that would warrant a dead-end.
        /// </para>
        /// </summary>
        private DecisionStep Dispatch(DecisionState state, DecisionSignal signal)
        {
            if (state.Stage == SessionStage.Completed || state.Stage == SessionStage.Failed)
            {
                return BuildPostTerminalDeadEnd(state, signal);
            }

            return (signal.Kind, signal.KindSchemaVersion) switch
            {
                // ----- Lifecycle (DecisionEngine.Shared.cs) -----
                (DecisionSignalKind.SessionStarted, 1)           => HandleSessionStartedV1(state, signal),
                (DecisionSignalKind.SessionAborted, 1)           => HandleSessionAbortedV1(state, signal),
                (DecisionSignalKind.AdminPreemptionDetected, 1)  => HandleAdminPreemptionDetectedV1(state, signal),
                (DecisionSignalKind.DeadlineFired, 1)            => HandleDeadlineFiredV1(state, signal),
                (DecisionSignalKind.EffectInfrastructureFailure, 1) => HandleEffectInfrastructureFailureV1(state, signal),

                // ----- Classic UserDriven-v1 (DecisionEngine.Classic.cs) -----
                (DecisionSignalKind.EspPhaseChanged, 1)          => HandleEspPhaseChangedV1(state, signal),
                (DecisionSignalKind.EspExiting, 1)               => HandleEspExitingV1(state, signal),
                (DecisionSignalKind.HelloResolved, 1)            => HandleHelloResolvedV1(state, signal),
                (DecisionSignalKind.DesktopArrived, 1)           => HandleDesktopArrivedV1(state, signal),
                (DecisionSignalKind.ImeUserSessionCompleted, 1)  => HandleImeUserSessionCompletedV1(state, signal),
                (DecisionSignalKind.AadUserJoinedLate, 1)        => HandleAadUserJoinedLateV1(state, signal),

                // ----- SelfDeploying + Device-Only (DecisionEngine.SelfDeploying.cs) -----
                (DecisionSignalKind.DeviceSetupProvisioningComplete, 1) => HandleDeviceSetupProvisioningCompleteV1(state, signal),

                // ----- AccountSetup completion gate (DecisionEngine.Shared.cs) — session 330f73f3 fix -----
                (DecisionSignalKind.AccountSetupProvisioningComplete, 1) => HandleAccountSetupProvisioningCompleteV1(state, signal),

                // ----- WhiteGlove Part 1 (DecisionEngine.WhiteGlove.cs) -----
                (DecisionSignalKind.WhiteGloveShellCoreSuccess, 1)         => HandleWhiteGloveShellCoreSuccessV1(state, signal),
                (DecisionSignalKind.WhiteGloveSealingPatternDetected, 1)   => HandleWhiteGloveSealingPatternDetectedV1(state, signal),
                (DecisionSignalKind.ClassifierVerdictIssued, 1)            => HandleClassifierVerdictIssuedV1(state, signal),

                // ----- Edge (DecisionEngine.Edge.cs) -----
                (DecisionSignalKind.EspResumed, 1)                         => HandleEspResumedV1(state, signal),
                (DecisionSignalKind.EspTerminalFailure, 1)                 => HandleEspTerminalFailureV1(state, signal),
                (DecisionSignalKind.SystemRebootObserved, 1)               => HandleSystemRebootObservedV1(state, signal),

                // ----- Diagnostic observations (DecisionEngine.Shared.cs) — Plan §4.x M4.4.3 -----
                (DecisionSignalKind.DeviceInfoCollected, 1)                => HandleDeviceInfoCollectedV1(state, signal),
                (DecisionSignalKind.AutopilotProfileRead, 1)               => HandleAutopilotProfileReadV1(state, signal),
                (DecisionSignalKind.EspConfigDetected, 1)                  => HandleEspConfigDetectedV1(state, signal),
                (DecisionSignalKind.HelloPolicyDetected, 1)                => HandleHelloPolicyDetectedV1(state, signal),
                (DecisionSignalKind.EnrollmentFactsObserved, 1)            => HandleEnrollmentFactsObservedV1(state, signal),

                // ----- Informational pass-through (DecisionEngine.Shared.cs) — single-rail §1.3 -----
                (DecisionSignalKind.InformationalEvent, 1)                 => HandleInformationalEventV1(state, signal),

                // ----- App-install observations (DecisionEngine.Shared.cs) — Codex follow-up #4 -----
                (DecisionSignalKind.AppInstallCompleted, 1)                => HandleAppInstallCompletedV1(state, signal),
                (DecisionSignalKind.AppInstallFailed, 1)                   => HandleAppInstallFailedV1(state, signal),

                // ----- Fall-through: unknown (kind, schemaVersion) pair → dead-end journal entry -----
                _ => HandleUnhandledSignal(state, signal),
            };
        }

        // ================================================================== helpers

        /// <summary>
        /// Advance StepIndex / LastAppliedSignalOrdinal without touching the engine-visible
        /// stage or facts. Reducer handlers call this when they record a step but don't
        /// progress the state machine (e.g. hypothesis-only update, or dead-end).
        /// </summary>
        internal DecisionState BumpStepBookkeeping(DecisionState state, DecisionSignal signal) =>
            state.ToBuilder()
                 .WithStepIndex(state.StepIndex + 1)
                 .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                 .Build();

        /// <summary>
        /// Build a <see cref="DecisionTransition"/> that records a state-changing step. Guards
        /// default to empty (for handler-initiated changes that aren't guard-driven); callers
        /// pass explicit guard evaluations when they matter to the Inspector.
        /// </summary>
        internal DecisionTransition BuildTakenTransition(
            DecisionState before,
            DecisionSignal signal,
            SessionStage toStage,
            int nextStepIndex,
            string trigger,
            IReadOnlyList<GuardReport>? guards = null)
        {
            return new DecisionTransition(
                stepIndex: nextStepIndex,
                sessionTraceOrdinal: signal.SessionTraceOrdinal,
                signalOrdinalRef: signal.SessionSignalOrdinal,
                occurredAtUtc: signal.OccurredAtUtc,
                trigger: trigger,
                fromStage: before.Stage,
                toStage: toStage,
                taken: true,
                deadEndReason: null,
                reducerVersion: ReducerVersion,
                guards: guards,
                emittedEventSequences: null,
                classifierVerdict: null,
                errorMessage: null,
                stackTraceHash: null);
        }

        internal DecisionTransition BuildDeadEndTransition(
            DecisionState state,
            DecisionSignal signal,
            int nextStepIndex,
            string trigger,
            string deadEndReason,
            IReadOnlyList<GuardReport>? guards = null)
        {
            return new DecisionTransition(
                stepIndex: nextStepIndex,
                sessionTraceOrdinal: signal.SessionTraceOrdinal,
                signalOrdinalRef: signal.SessionSignalOrdinal,
                occurredAtUtc: signal.OccurredAtUtc,
                trigger: trigger,
                fromStage: state.Stage,
                toStage: state.Stage,
                taken: false,
                deadEndReason: deadEndReason,
                reducerVersion: ReducerVersion,
                guards: guards,
                emittedEventSequences: null,
                classifierVerdict: null,
                errorMessage: null,
                stackTraceHash: null);
        }

        /// <summary>
        /// Plan §2.5 L.16 — exception fail-safe. State is unchanged (no builder copy); only
        /// bookkeeping (StepIndex, LastAppliedSignalOrdinal) advances so the signal counts as
        /// processed. <see cref="DecisionTransition.ErrorMessage"/> holds the original message
        /// and <see cref="DecisionTransition.StackTraceHash"/> a short SHA256 of the trace —
        /// enough for an Inspector to link recurring-exception rows without dumping full PII.
        /// </summary>
        private DecisionStep BuildReducerExceptionStep(
            DecisionState state,
            DecisionSignal signal,
            Exception exception)
        {
            var bookkept = BumpStepBookkeeping(state, signal);
            var transition = new DecisionTransition(
                stepIndex: bookkept.StepIndex,
                sessionTraceOrdinal: signal.SessionTraceOrdinal,
                signalOrdinalRef: signal.SessionSignalOrdinal,
                occurredAtUtc: signal.OccurredAtUtc,
                trigger: signal.Kind.ToString(),
                fromStage: state.Stage,
                toStage: state.Stage,
                taken: false,
                deadEndReason: "reducer_exception",
                reducerVersion: ReducerVersion,
                guards: null,
                emittedEventSequences: null,
                classifierVerdict: null,
                errorMessage: exception.Message,
                stackTraceHash: HashStackTrace(exception));

            return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
        }

        private DecisionStep HandleUnhandledSignal(DecisionState state, DecisionSignal signal)
        {
            var bookkept = BumpStepBookkeeping(state, signal);
            var transition = BuildDeadEndTransition(
                state: state,
                signal: signal,
                nextStepIndex: bookkept.StepIndex,
                trigger: signal.Kind.ToString(),
                deadEndReason: $"unhandled_signal_kind:{signal.Kind}:v{signal.KindSchemaVersion}");

            return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Bookkeeping-only step for signals that arrive after the session reached a classic
        /// terminal stage. State (Stage / Outcome / facts / deadlines) is unchanged; only
        /// StepIndex and LastAppliedSignalOrdinal advance so the signal is recorded as
        /// processed in the SignalLog. <c>DeadEndReason="signal_after_terminal:&lt;stage&gt;"</c>
        /// lets the Inspector pinpoint which late-arriving signal would have re-entered the
        /// terminal handlers without this guard.
        /// </summary>
        private DecisionStep BuildPostTerminalDeadEnd(DecisionState state, DecisionSignal signal)
        {
            var bookkept = BumpStepBookkeeping(state, signal);
            var transition = BuildDeadEndTransition(
                state: state,
                signal: signal,
                nextStepIndex: bookkept.StepIndex,
                trigger: signal.Kind.ToString(),
                deadEndReason: $"signal_after_terminal:{state.Stage}");

            return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
        }

        private static string HashStackTrace(Exception exception)
        {
            var raw = exception.StackTrace ?? exception.GetType().FullName ?? "unknown";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
