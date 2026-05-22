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
        /// Whitelist of <c>parameters</c>-keys that are merged into the audit-trail typed payload
        /// for ESP-failure-related timeline events (PR1, Session 4fa5a2d4). Emitter-metadata
        /// keys like <c>eventType</c>, <c>severity</c>, <c>source</c>, <c>phase</c> are
        /// deliberately NOT in the whitelist — they belong on the event header, not duplicated
        /// into wire-data. Codex review finding #18.
        /// </summary>
        private static readonly string[] FailureContextKeysWhitelist = new[]
        {
            "failureType",
            "errorCode",
            "failedSubcategory",
            "category",
            "espSyncFailureTimeoutMinutes",
            "espAllowContinueAnyway",
            "mayHaveContinuedAnyway",
            "continueAnywayHint",
            "advisoryReason",
        };

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EspTerminalFailure"/>. Two paths:
        /// <list type="bullet">
        ///   <item>
        ///   <b>Advisory defang</b> — when the ESP profile permits "Continue anyway"
        ///   (<c>ScenarioObservations.EspAllowContinueAnyway == true</c>) AND
        ///   <see cref="DecisionState.AccountSetupEnteredUtc"/> is already set, the device has
        ///   demonstrably progressed past DeviceSetup. The signal is downgraded to an
        ///   <c>esp_failure_advisory</c> timeline entry, the stage stays unchanged, deadlines
        ///   remain armed, and normal completion paths (Hello/Desktop, IME pattern,
        ///   AccountSetupProvisioningComplete) continue to drive the session. Session 4fa5a2d4
        ///   (2026-05-22) — defangs the 199/202 false-positive failures observed in tenant
        ///   c9787ba2 where ContinueAnyway was on and the device kept progressing.
        ///   </item>
        ///   <item>
        ///   <b>Terminal failure</b> — default path. Transitions to <see cref="SessionStage.Failed"/>
        ///   with <see cref="SessionOutcome.EnrollmentFailed"/>, clears deadlines, and emits
        ///   <c>enrollment_failed</c>. No classifier re-run — ESP terminal failure is definitive.
        ///   </item>
        /// </list>
        /// Duplicates: once <c>EspAdvisoryFailureRecordedUtc</c> is set, subsequent
        /// <c>EspTerminalFailure</c> signals (e.g. from <c>ShellCoreTracker</c> in addition to
        /// <c>ProvisioningStatusTracker</c>) dead-end without effects so the timeline does not
        /// accumulate redundant advisories.
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
            // After an advisory was recorded: a duplicate EspTerminalFailure from another tracker
            // source may either be a true duplicate (same context, no new info) OR an enrichment
            // (e.g. ShellCoreTracker fired first with sparse payload, then ProvisioningStatusTracker
            // fires later with the registry-derived errorCode/failedSubcategory/category). The two
            // sources can fire in either order (eventlog and registry race). We don't want to
            // drop the richer payload if it arrives second — that would hide the HRESULT and
            // subcategory the UI/KQL need.
            //
            // Gate the enrichment on registry-specific keys ONLY (errorCode/failedSubcategory/
            // category). failureType is posted by BOTH ShellCoreTracker and ProvisioningStatusTracker
            // (their adapters always include it), so a ShellCoreTracker signal arriving AFTER a
            // rich Provisioning advisory would otherwise be misclassified as enrichment and emit
            // a redundant follow-up event.
            if (state.EspAdvisoryFailureRecordedUtc != null)
            {
                var carriesRegistryDetail = signal.Payload != null && (
                    signal.Payload.ContainsKey("errorCode")
                    || signal.Payload.ContainsKey("failedSubcategory")
                    || signal.Payload.ContainsKey("category"));

                if (carriesRegistryDetail)
                {
                    return BuildAdvisoryEnrichmentStep(state, signal);
                }

                var dupNextStep = state.StepIndex + 1;
                var dupState = BumpStepBookkeeping(state, signal);
                var dupTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: dupNextStep,
                    trigger: nameof(DecisionSignalKind.EspTerminalFailure),
                    deadEndReason: "esp_terminal_failure_advisory_already_recorded");
                return new DecisionStep(dupState, dupTransition, Array.Empty<DecisionEffect>());
            }

            var nextStep = state.StepIndex + 1;

            // Default to the stable enum literal so the timeline-event "reason" is predictable
            // across all sources of EspTerminalFailure. Direct test signals (and any future
            // adapter that wants to override) can still inject a specific reason via payload.
            var reason = signal.Payload != null
                && signal.Payload.TryGetValue("reason", out var r)
                && !string.IsNullOrEmpty(r)
                ? r
                : "esp_terminal_failure";

            // Build parameter bag once — both paths consume it via BuildEspFailureAuditTrail.
            var observations = state.ScenarioObservations;
            var continueAnywayEnabled = observations?.EspAllowContinueAnyway?.Value == true;
            var advisoryEligible = continueAnywayEnabled && state.AccountSetupEnteredUtc != null;
            var parameters = BuildEspFailureParameters(
                signal: signal,
                reason: reason,
                advisoryPath: advisoryEligible,
                observations: observations);

            if (advisoryEligible)
            {
                return BuildAdvisoryStep(state, signal, nextStep, reason, parameters);
            }

            return BuildFailedStep(state, signal, nextStep, reason, parameters);
        }

        /// <summary>
        /// Build the advisory-enrichment step that runs when an EspTerminalFailure duplicate
        /// arrives AFTER an advisory was already recorded AND the duplicate carries new
        /// failure-context (failureType/errorCode/failedSubcategory/category). Stage and the
        /// EspAdvisoryFailureRecordedUtc-fact stay unchanged — the first signal's anchor wins.
        /// A second <c>esp_failure_advisory</c> event is emitted carrying the enriched payload
        /// so the timeline / UI gain the registry-derived HRESULT and subcategory detail.
        /// </summary>
        private DecisionStep BuildAdvisoryEnrichmentStep(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var reason = signal.Payload != null
                && signal.Payload.TryGetValue("reason", out var r)
                && !string.IsNullOrEmpty(r)
                ? r
                : "esp_terminal_failure";

            var parameters = BuildEspFailureParameters(
                signal: signal,
                reason: reason,
                advisoryPath: true,
                observations: state.ScenarioObservations);

            // Override advisoryReason so the timeline can distinguish "first advisory at gate-set"
            // from "later enrichment of the same advisory". Both events share eventType so the
            // UI render path is identical.
            parameters["advisoryReason"] = "esp_failure_advisory_enriched_from_duplicate";

            // Bookkeeping-only state update — the advisory-anchor (EspAdvisoryFailureRecordedUtc)
            // intentionally stays at the first signal's ordinal/UTC.
            var newState = BumpStepBookkeeping(state, signal);

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspTerminalFailure));

            var effects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: parameters,
                    typedPayload: BuildEspFailureAuditTrail(
                        postState: newState,
                        decidedStage: state.Stage,
                        trigger: nameof(DecisionSignalKind.EspTerminalFailure),
                        failureReason: reason,
                        parameters: parameters)),
            };

            return new DecisionStep(newState, transition, effects);
        }

        private DecisionStep BuildAdvisoryStep(
            DecisionState state,
            DecisionSignal signal,
            int nextStep,
            string reason,
            Dictionary<string, string> parameters)
        {
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithEspAdvisoryFailureRecorded(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            // Stage / Outcome / Deadlines deliberately untouched — monitoring continues. The
            // session's normal completion paths (Hello/Desktop, IME pattern, AccountSetup
            // provisioning complete) still drive it.
            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspTerminalFailure));

            var effects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: parameters,
                    typedPayload: BuildEspFailureAuditTrail(
                        postState: newState,
                        decidedStage: state.Stage,
                        trigger: nameof(DecisionSignalKind.EspTerminalFailure),
                        failureReason: reason,
                        parameters: parameters)),
            };

            return new DecisionStep(newState, transition, effects);
        }

        private DecisionStep BuildFailedStep(
            DecisionState state,
            DecisionSignal signal,
            int nextStep,
            string reason,
            Dictionary<string, string> parameters)
        {
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

            var effects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: parameters,
                    typedPayload: BuildEspFailureAuditTrail(
                        postState: newState,
                        decidedStage: SessionStage.Failed,
                        trigger: nameof(DecisionSignalKind.EspTerminalFailure),
                        failureReason: reason,
                        parameters: parameters)),
            };

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Build the timeline-event parameter bag for the advisory and failed paths. The
        /// <c>eventType</c> distinguishes the two; failure-context keys
        /// (failureType/errorCode/failedSubcategory/category) carry the registry-derived detail
        /// from the originating ESP signal so the UI / KQL can render the HRESULT badge without
        /// re-parsing the ESP statusText. ContinueAnyway hint keys are only added when the
        /// ScenarioObservations actually carry an <c>EspAllowContinueAnyway</c> fact — absence
        /// of those keys distinguishes "ContinueAnyway unknown" from "ContinueAnyway explicitly
        /// false" in the wire payload.
        /// </summary>
        private static Dictionary<string, string> BuildEspFailureParameters(
            DecisionSignal signal,
            string reason,
            bool advisoryPath,
            State.EnrollmentScenarioObservations? observations)
        {
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = advisoryPath
                    ? SharedConstants.EventTypes.EspFailureAdvisory
                    : "enrollment_failed",
                ["reason"] = reason,
            };

            if (advisoryPath)
            {
                // Advisory is a non-terminal hint: ESP reported a failure but the device kept
                // progressing past it. Warning matches the UI's amber badge and the semantic
                // weight ("something worth knowing but not session-fatal"). The Failed path
                // relies on the emitter's DeriveSeverity("_failed" → Error) default.
                parameters["severity"] = "Warning";
                parameters["advisoryReason"] = "esp_failure_defanged_continueanyway_with_accountsetup";
            }

            // Session 9d052230 — surface registry-derived ESP failure detail (HRESULT etc) on
            // the terminal event so the UI can render it without parsing nested ESP statusText.
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

            // FirstSync-derived ESP failure-handling settings — observable hint that
            // Microsoft's ESP profile may have offered the user a "Continue anyway" button.
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
                    parameters["continueAnywayHint"] = advisoryPath
                        ? "ESP reported a subcategory failure but the device had already progressed to AccountSetup; the agent continues monitoring instead of declaring the session failed."
                        : "ESP profile allows 'Continue anyway' — the user may have dismissed the failure screen and reached the desktop; this monitor only sees the ESP terminal failure on the agent side.";
                }
            }

            return parameters;
        }

        /// <summary>
        /// Builds the audit-trail typed payload for ESP-failure timeline events by combining
        /// <see cref="DecisionAuditTrailBuilder.Build"/> with a whitelisted merge of
        /// failure-context keys from <paramref name="parameters"/>. The base builder owns
        /// scenario/signal-census/trigger fields; this helper layers in the registry-derived
        /// detail (failureType/errorCode/failedSubcategory/category) plus the conditional
        /// ContinueAnyway hint keys. Emitter-metadata keys (<c>eventType</c>, <c>severity</c>,
        /// etc.) are NOT merged — they belong on the event header. Codex review finding #18.
        /// </summary>
        private static Dictionary<string, object> BuildEspFailureAuditTrail(
            DecisionState postState,
            SessionStage decidedStage,
            string trigger,
            string failureReason,
            IReadOnlyDictionary<string, string> parameters)
        {
            var trail = DecisionAuditTrailBuilder.Build(
                postState: postState,
                decidedStage: decidedStage,
                trigger: trigger,
                failureReason: failureReason);
            for (var i = 0; i < FailureContextKeysWhitelist.Length; i++)
            {
                var key = FailureContextKeysWhitelist[i];
                if (parameters.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                {
                    trail[key] = value;
                }
            }
            return trail;
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
