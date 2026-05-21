using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // Edge-case handlers: hybrid reboot, ESP terminal failure, system reboot observation.
    // Plan §2.5 / §M3.5.
    public sealed partial class DecisionEngine
    {
        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EspResumed"/>. Emitted by
        /// <c>EspAndHelloTracker</c> after a reboot mid-ESP. The session's stage is preserved
        /// (we were already somewhere in the ESP phase sequence), but the reboot fact is
        /// recorded so downstream classifiers (WhiteGloveSealingClassifier) can factor it in.
        /// </summary>
        private DecisionStep HandleEspResumedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            // Record the reboot fact on first observation. Subsequent EspResumed signals
            // keep the original timestamp so the hash over SystemRebootUtc is stable.
            if (state.SystemRebootUtc == null)
            {
                builder.SystemRebootUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            }

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspResumed));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EspTerminalFailure"/>. Directly transitions
        /// to <see cref="SessionStage.Failed"/> with <see cref="SessionOutcome.EnrollmentFailed"/>,
        /// clears deadlines, and emits <c>enrollment_failed</c>. No classifier re-run —
        /// ESP terminal failure is definitive.
        /// <para>
        /// The terminal-event reason is always the stable enum literal <c>esp_terminal_failure</c>.
        /// Session 9d052230: registry-derived failures additionally carry <c>failureType</c>,
        /// <c>errorCode</c> (HRESULT), <c>failedSubcategory</c>, and <c>category</c> on the signal
        /// payload — those propagate verbatim to the timeline-event parameters so the UI can
        /// render the HRESULT badge + description without re-parsing the ESP statusText.
        /// </para>
        /// </summary>
        private DecisionStep HandleEspTerminalFailureV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;

            // Default to the stable enum literal so the timeline-event "reason" is predictable
            // across all sources of EspTerminalFailure. Direct test signals (and any future
            // adapter that wants to override) can still inject a specific reason via payload.
            var reason = signal.Payload != null
                && signal.Payload.TryGetValue("reason", out var r)
                && !string.IsNullOrEmpty(r)
                ? r
                : "esp_terminal_failure";

            var builder = state.ToBuilder()
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithLastFailureTrigger(nameof(DecisionSignalKind.EspTerminalFailure), signal.SessionSignalOrdinal)
                .ClearDeadlines();

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Failed,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspTerminalFailure));

            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = "enrollment_failed",
                ["reason"] = reason,
            };

            // Session 9d052230 — surface registry-derived ESP failure detail (HRESULT etc) on the
            // terminal event so the UI can render it without parsing nested ESP statusText.
            if (signal.Payload != null)
            {
                if (signal.Payload.TryGetValue("failureType", out var failureType) && !string.IsNullOrEmpty(failureType))
                    parameters["failureType"] = failureType;
                if (signal.Payload.TryGetValue("errorCode", out var errorCode) && !string.IsNullOrEmpty(errorCode))
                    parameters["errorCode"] = errorCode;
                if (signal.Payload.TryGetValue("failedSubcategory", out var failedSubcategory) && !string.IsNullOrEmpty(failedSubcategory))
                    parameters["failedSubcategory"] = failedSubcategory;
                if (signal.Payload.TryGetValue("category", out var category) && !string.IsNullOrEmpty(category))
                    parameters["category"] = category;
            }

            // Enrich the audit payload with the FirstSync-derived ESP failure-handling settings,
            // so the timeline carries the observable fact that Microsoft's ESP profile may have
            // offered the user a "Continue anyway" button — the agent's terminal-failure verdict
            // does not preclude the user actually reaching the desktop. This is hint-only data;
            // the reducer's state machine has already committed to Failed.
            var observations = newState.ScenarioObservations;
            if (observations?.EspSyncFailureTimeoutMinutes != null)
            {
                parameters["espSyncFailureTimeoutMinutes"] = observations.EspSyncFailureTimeoutMinutes.Value
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (observations?.EspAllowContinueAnyway != null)
            {
                parameters["espAllowContinueAnyway"] = observations.EspAllowContinueAnyway.Value
                    ? "true"
                    : "false";
                if (observations.EspAllowContinueAnyway.Value)
                {
                    parameters["mayHaveContinuedAnyway"] = "true";
                    parameters["continueAnywayHint"] =
                        "ESP profile allows 'Continue anyway' — the user may have dismissed the failure screen and reached the desktop; this monitor only sees the ESP terminal failure on the agent side.";
                }
            }

            var effects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: parameters,
                    typedPayload: DecisionAuditTrailBuilder.Build(
                        postState: newState,
                        decidedStage: SessionStage.Failed,
                        trigger: nameof(DecisionSignalKind.EspTerminalFailure),
                        failureReason: reason)),
            };

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SystemRebootObserved"/>. Records the fact so
        /// WhiteGlove scoring (plan §2.4) can credit the +15 reboot-observed weight, and emits
        /// the <c>system_reboot_detected</c> timeline entry so the session timeline shows the
        /// split between pre-reboot and post-reboot collection.
        /// </summary>
        private DecisionStep HandleSystemRebootObservedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            if (state.SystemRebootUtc == null)
            {
                builder.SystemRebootUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            }

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.SystemRebootObserved));

            var timelineParams = new Dictionary<string, string>
            {
                ["eventType"] = SharedConstants.EventTypes.SystemRebootDetected,
                ["reason"] = "prior agent process was terminated by the reboot",
            };
            if (signal.Payload != null)
            {
                if (signal.Payload.TryGetValue("previousExitType", out var previousExitType))
                    timelineParams["previousExitType"] = previousExitType ?? string.Empty;
                if (signal.Payload.TryGetValue("lastBootUtc", out var lastBootUtc))
                    timelineParams["lastBootUtc"] = lastBootUtc ?? string.Empty;
            }

            var effects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: timelineParams),
            };

            return new DecisionStep(newState, transition, effects);
        }
    }
}
