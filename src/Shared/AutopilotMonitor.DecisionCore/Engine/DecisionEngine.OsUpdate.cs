using System;
using System.Collections.Generic;
using System.Globalization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // OOBE update phase (D-310). With the ESP setting "Install Windows quality updates" Windows
    // runs an update page at the end of OOBE — after the Device-ESP page exits and before the
    // user signs in (CloudExperienceHost nodes OobeNDUP → RebootNDUP → OobeExit). Microsoft
    // documents 20–40 minutes plus a restart, after which the user must sign in again at the lock
    // screen. When the IME logged its AccountSetup phase line before that handoff exit (it does so
    // as soon as the device phase completes), the exit armed the AdvisoryCompletion window and
    // the session was failed mid-update (report 3a2207978e4c).
    //
    // The phase holds that window, but every hold is bounded on its own — the agent's max
    // lifetime is no bound, it restarts with every agent start:
    //   - the update runs:  at most OobeUpdateCap after the first update activity, then the
    //                       session fails as oobe_update_not_finished;
    //   - the update ended: OobeSignInWindow for the sign-in, then the engine stops waiting
    //                       (StoppedWaitingReason) and the agent ends the session like its
    //                       max-lifetime watchdog — the backend classifies it honestly, never Failed;
    //   - the user arrived: one AdvisoryCompletion window of grace for the user's own setup.
    public sealed partial class DecisionEngine
    {
        internal static readonly TimeSpan s_oobeUpdateCap = TimeSpan.FromMinutes(OobeUpdatePhaseBounds.UpdateCapMinutes);

        internal static readonly TimeSpan s_oobeSignInWindow = TimeSpan.FromMinutes(OobeUpdatePhaseBounds.SignInWindowMinutes);

        /// <summary>
        /// Record one Windows/.NET update step. The earliest activity after the ESP exit while no
        /// real user has signed in anchors the OOBE update phase
        /// (<see cref="OsUpdateFacts.PostExitActivityUtc"/>). Bookkeeping only: the stage stays,
        /// no deadline is armed — the phase only changes how
        /// <see cref="HandleAdvisoryCompletionDeadlineFired"/> resolves.
        /// </summary>
        private DecisionStep HandleOsUpdateActivityV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;

            string? step = null;
            string? update = null;
            signal.Payload?.TryGetValue(OsUpdatePayloadKeys.Step, out step);
            signal.Payload?.TryGetValue(OsUpdatePayloadKeys.Update, out update);

            if (string.IsNullOrEmpty(step))
            {
                var bookkept = BumpStepBookkeeping(state, signal);
                return new DecisionStep(
                    bookkept,
                    BuildDeadEndTransition(state, signal, bookkept.StepIndex,
                        trigger: nameof(DecisionSignalKind.OsUpdateActivity),
                        deadEndReason: "os_update_activity_without_step"),
                    Array.Empty<DecisionEffect>());
            }

            var phaseActivity = IsOobeUpdatePhaseActivity(state, signal);
            var phaseStarts = phaseActivity && state.OsUpdateFacts.PostExitActivityUtc == null;

            var facts = state.OsUpdateFacts.WithActivity(
                signal.OccurredAtUtc, signal.SessionSignalOrdinal, step!, update, phaseActivity);

            var newState = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithOsUpdateFacts(facts)
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.OsUpdateActivity) + (phaseStarts ? ":OobeUpdatePhaseStarted" : string.Empty));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Record a system restart that belongs to the OOBE update phase — the update page ends
        /// with one. Called by the reboot handlers; a restart before the anchor or after a real
        /// user arrived is not the update's restart.
        /// </summary>
        private static void RecordOobeUpdateRestart(DecisionState state, DecisionStateBuilder builder, DecisionSignal signal)
        {
            var anchor = state.OsUpdateFacts.PostExitActivityUtc;
            if (anchor == null || signal.OccurredAtUtc < anchor.Value || HasRealUserEvidence(state)) return;
            builder.WithOsUpdateFacts(state.OsUpdateFacts.WithRestart(signal.OccurredAtUtc, signal.SessionSignalOrdinal));
        }

        /// <summary>
        /// True while the session is in the OOBE update phase: OS update activity was observed
        /// after the ESP exit and no real user has signed in since. Says nothing about the bounds —
        /// <see cref="EvaluateOobeUpdatePhase"/> decides what the phase holds.
        /// </summary>
        internal static bool IsInOobeUpdatePhase(DecisionState state) =>
            state.OsUpdateFacts.PostExitActivityUtc != null && !HasRealUserEvidence(state);

        internal enum OobeUpdatePhaseKind
        {
            /// <summary>No OOBE update phase in this session.</summary>
            None,
            /// <summary>The update runs; holds until the cap.</summary>
            Updating,
            /// <summary>The update ended; holds until the sign-in window closes.</summary>
            AwaitingSignIn,
            /// <summary>The user signed in less than one window ago; holds for the user's setup.</summary>
            UserArrived,
            /// <summary>The cap passed while the update still ran — the session fails as not finished.</summary>
            UpdateNotFinished,
            /// <summary>Nobody signed in within the sign-in window — the engine stops waiting.</summary>
            SignInWaitExpired,
            /// <summary>The user's grace passed; the window resolves the normal way.</summary>
            Over,
        }

        internal readonly struct OobeUpdatePhaseStatus
        {
            public OobeUpdatePhaseStatus(OobeUpdatePhaseKind kind, DateTime? boundUtc = null, DateTime? updateEndedUtc = null)
            {
                Kind = kind;
                BoundUtc = boundUtc;
                UpdateEndedUtc = updateEndedUtc;
            }

            public OobeUpdatePhaseKind Kind { get; }

            /// <summary>When the current state stops holding (cap, end of sign-in window or grace).</summary>
            public DateTime? BoundUtc { get; }

            /// <summary>When the update ended (its restart or terminal step); null while it runs.</summary>
            public DateTime? UpdateEndedUtc { get; }

            public bool Holds =>
                Kind == OobeUpdatePhaseKind.Updating
                || Kind == OobeUpdatePhaseKind.AwaitingSignIn
                || Kind == OobeUpdatePhaseKind.UserArrived;
        }

        /// <summary>
        /// Where the OOBE update phase stands at <paramref name="nowUtc"/>. The update counts as
        /// ended when its latest event — by event time — is its restart or a terminal step
        /// (installed, failed, reboot_required); any newer non-terminal step means it runs (again).
        /// </summary>
        internal static OobeUpdatePhaseStatus EvaluateOobeUpdatePhase(DecisionState state, DateTime nowUtc)
        {
            var facts = state.OsUpdateFacts;
            var anchor = facts.PostExitActivityUtc;
            if (anchor == null) return new OobeUpdatePhaseStatus(OobeUpdatePhaseKind.None);

            var userArrivedUtc = FirstRealUserEvidenceUtc(state);
            if (userArrivedUtc != null)
            {
                var graceEnd = userArrivedUtc.Value + s_advisoryCompletionWindow;
                return nowUtc < graceEnd
                    ? new OobeUpdatePhaseStatus(OobeUpdatePhaseKind.UserArrived, graceEnd)
                    : new OobeUpdatePhaseStatus(OobeUpdatePhaseKind.Over);
            }

            var endedUtc = UpdateEndedUtc(facts);
            if (endedUtc == null)
            {
                var capEnd = anchor.Value + s_oobeUpdateCap;
                return new OobeUpdatePhaseStatus(
                    nowUtc < capEnd ? OobeUpdatePhaseKind.Updating : OobeUpdatePhaseKind.UpdateNotFinished,
                    capEnd);
            }

            var signInEnd = endedUtc.Value + s_oobeSignInWindow;
            return new OobeUpdatePhaseStatus(
                nowUtc < signInEnd ? OobeUpdatePhaseKind.AwaitingSignIn : OobeUpdatePhaseKind.SignInWaitExpired,
                signInEnd,
                endedUtc);
        }

        private static DateTime? UpdateEndedUtc(OsUpdateFacts facts)
        {
            var last = facts.LastActivityUtc?.Value;
            var restart = facts.RestartUtc?.Value;
            if (restart != null && (last == null || restart.Value >= last.Value)) return restart;
            if (last != null && OsUpdateSteps.IsTerminal(facts.LastStep?.Value)) return last;
            return null;
        }

        /// <summary>
        /// The <c>completion_waiting</c> trigger suffix and message for a holding phase state.
        /// </summary>
        private static (string TriggerSuffix, string Message) DescribeOobeUpdateHold(OobeUpdatePhaseKind kind)
        {
            switch (kind)
            {
                case OobeUpdatePhaseKind.Updating:
                    return ("OobeUpdatePhase",
                        "Completion resolution window re-armed: Windows is installing an update in OOBE");
                case OobeUpdatePhaseKind.AwaitingSignIn:
                    return ("OobeUpdateAwaitingSignIn",
                        "Completion resolution window re-armed: the Windows update in OOBE has finished, waiting for the user to sign in");
                default:
                    return ("OobeUpdatePhaseUserArrived",
                        "Completion resolution window re-armed: the user signed in after the OOBE update and gets one full window for the own setup");
            }
        }

        /// <summary>
        /// Nobody signed in within the sign-in window after the OOBE update: stop waiting. The
        /// stage stays non-terminal and no verdict is declared — the device is fine, the user is
        /// absent. <see cref="DecisionState.StoppedWaitingReason"/> makes the agent end the session
        /// like its max-lifetime watchdog; the backend then classifies it (AwaitingUser for a
        /// WhiteGlove Part 2, otherwise Incomplete), never Failed.
        /// </summary>
        private DecisionStep BuildOobeUpdateStopWaitingStep(
            DecisionState state,
            DecisionStateBuilder builder,
            DecisionSignal signal,
            int nextStep,
            OobeUpdatePhaseStatus phase)
        {
            builder.StoppedWaitingReason = new SignalFact<string>(
                StoppedWaitingReasons.OobeUpdateNoSignIn, signal.SessionSignalOrdinal);
            builder.ClearDeadlines();

            var trigger = $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}:OobeUpdateSignInWaitExpired";
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = SharedConstants.EventTypes.CompletionWaitExpired,
                ["source"] = "DecisionEngine",
                ["severity"] = "Warning",
                ["immediateUpload"] = "true",
                ["message"] = "Nobody signed in within " +
                    s_oobeSignInWindow.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) +
                    " minutes after the Windows update in OOBE — the agent stops waiting. Not a failure: " +
                    "the session is classified as awaiting the user or incomplete.",
                ["reason"] = StoppedWaitingReasons.OobeUpdateNoSignIn,
                ["signInWindowMinutes"] = s_oobeSignInWindow.TotalMinutes.ToString("0", CultureInfo.InvariantCulture),
                ["stage"] = state.Stage.ToString(),
            };
            AddOobeUpdateContext(parameters, state, phase.UpdateEndedUtc);

            var stoppedState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: trigger);

            return new DecisionStep(stoppedState, transition, new[]
            {
                new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry, parameters: parameters),
            });
        }

        /// <summary>
        /// Shadow mode: what the phase would decide at this advisory fire, emitted right before
        /// today's verdict (<paramref name="decidedReason"/>). At most once per session — in Shadow
        /// mode the fire that reaches the phase guard always ends in that verdict.
        /// </summary>
        private static DecisionEffect BuildOobeUpdateShadowEffect(
            DecisionState state,
            OobeUpdatePhaseStatus phase,
            string decidedReason)
        {
            var (phaseState, wouldDecide, outlook) = DescribeOobeUpdateShadow(phase.Kind);
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = SharedConstants.EventTypes.OobeUpdatePhaseShadow,
                ["source"] = "DecisionEngine",
                ["severity"] = "Debug",
                ["immediateUpload"] = "true",
                ["message"] = $"OOBE update phase (shadow): {outlook} Today's rule decides ({decidedReason}).",
                ["phaseMode"] = OobeUpdatePhaseModes.Shadow,
                ["phaseState"] = phaseState,
                ["wouldDecide"] = wouldDecide,
                ["decidedReason"] = decidedReason,
                ["trigger"] = $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}",
                ["stage"] = state.Stage.ToString(),
            };
            if (phase.BoundUtc != null)
                parameters["phaseBoundUtc"] = phase.BoundUtc.Value.ToString("o");
            var userArrivedUtc = FirstRealUserEvidenceUtc(state);
            if (userArrivedUtc != null)
                parameters["userArrivedUtc"] = userArrivedUtc.Value.ToString("o");
            AddOobeUpdateContext(parameters, state, phase.UpdateEndedUtc);
            return new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry, parameters: parameters);
        }

        /// <summary><c>phaseState</c>, <c>wouldDecide</c> and the message part of a shadow evaluation.</summary>
        private static (string PhaseState, string WouldDecide, string Outlook) DescribeOobeUpdateShadow(OobeUpdatePhaseKind kind)
        {
            var capHours = s_oobeUpdateCap.TotalHours.ToString("0", CultureInfo.InvariantCulture);
            var signInMinutes = s_oobeSignInWindow.TotalMinutes.ToString("0", CultureInfo.InvariantCulture);
            switch (kind)
            {
                case OobeUpdatePhaseKind.Updating:
                    return ("update_running", "hold",
                        $"Windows is still installing an update in OOBE; the phase would keep waiting (cap {capHours} h).");
                case OobeUpdatePhaseKind.AwaitingSignIn:
                    return ("awaiting_sign_in", "hold",
                        $"the Windows update in OOBE has finished; the phase would wait {signInMinutes} minutes for the sign-in.");
                case OobeUpdatePhaseKind.UserArrived:
                    return ("user_arrived", "hold",
                        "the user signed in after the OOBE update; the phase would give the user's own setup one more window.");
                case OobeUpdatePhaseKind.UpdateNotFinished:
                    return ("update_not_finished", "fail_oobe_update_not_finished",
                        $"the update still ran after {capHours} hours; the phase would fail the session as oobe_update_not_finished.");
                case OobeUpdatePhaseKind.SignInWaitExpired:
                    return ("sign_in_wait_expired", "stop_waiting",
                        $"nobody signed in within {signInMinutes} minutes after the update; the phase would stop waiting without a verdict.");
                default:
                    return ("over", "unchanged",
                        "the user's window after the OOBE update has passed; the phase would decide the same.");
            }
        }

        /// <summary>
        /// Failure parameters for an OOBE update that still ran when the cap passed — a hanging
        /// update, reported with its own reason instead of <c>esp_exit_without_completion_evidence</c>.
        /// </summary>
        private static Dictionary<string, string> BuildOobeUpdateNotFinishedParameters(DecisionState state)
        {
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = SharedConstants.EventTypes.EnrollmentFailed,
                ["reason"] = "oobe_update_not_finished",
                ["advisoryReason"] = "oobe_update_cap_expired_without_restart",
                ["oobeUpdateCapHours"] = s_oobeUpdateCap.TotalHours.ToString("0", CultureInfo.InvariantCulture),
            };
            AddOobeUpdateContext(parameters, state, updateEndedUtc: null);
            return parameters;
        }

        private static void AddOobeUpdateContext(Dictionary<string, string> parameters, DecisionState state, DateTime? updateEndedUtc)
        {
            var facts = state.OsUpdateFacts;
            if (facts.PostExitActivityUtc != null)
                parameters["oobeUpdatePhaseStartedUtc"] = facts.PostExitActivityUtc.Value.ToString("o");
            if (updateEndedUtc != null)
                parameters["oobeUpdateEndedUtc"] = updateEndedUtc.Value.ToString("o");
            if (facts.LastStep != null)
                parameters["osUpdateLastStep"] = facts.LastStep.Value;
            if (facts.LastUpdate != null)
                parameters["osUpdate"] = facts.LastUpdate.Value;
        }

        /// <summary>
        /// Real-user evidence: a DAD-validated desktop, a Hello wizard start or resolution, or a
        /// confirmed Account Setup. Deliberately NOT evidence: the IME AccountSetup phase line and
        /// the IME user-session completion — IME writes both under <c>defaultuser0</c> before the
        /// sign-in (session 1924092e, report 3a2207978e4c).
        /// </summary>
        internal static bool HasRealUserEvidence(DecisionState state) => FirstRealUserEvidenceUtc(state) != null;

        private static DateTime? FirstRealUserEvidenceUtc(DecisionState state)
        {
            DateTime? first = null;
            foreach (var fact in new[]
            {
                state.DesktopArrivedUtc,
                state.HelloWizardStartedUtc,
                state.HelloResolvedUtc,
                state.AccountSetupProvisioningSucceededUtc,
            })
            {
                if (fact != null && (first == null || fact.Value < first.Value)) first = fact.Value;
            }
            return first;
        }

        private static bool IsOobeUpdatePhaseActivity(DecisionState state, DecisionSignal signal)
        {
            var exit = state.EspFinalExitUtc;
            if (exit == null) return false;
            if (signal.SessionSignalOrdinal <= exit.SourceSignalOrdinal) return false;
            // The watchers backfill their logs after an agent restart: such a record gets a fresh
            // ordinal but keeps its event-log time, so activity that predates the exit is not
            // part of the phase.
            if (signal.OccurredAtUtc < exit.Value) return false;
            return !HasRealUserEvidence(state);
        }
    }
}
