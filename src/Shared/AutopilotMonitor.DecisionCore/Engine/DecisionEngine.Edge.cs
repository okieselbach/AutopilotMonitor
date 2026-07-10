using System;
using System.Collections.Generic;
using System.Globalization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
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
        /// A reboot also cancels an esp-exit-variant AdvisoryCompletion window — see
        /// <see cref="CancelEspExitVariantAdvisoryWindowOnReboot"/>.
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

            var cancelEffect = CancelEspExitVariantAdvisoryWindowOnReboot(state, builder);

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspResumed));

            return new DecisionStep(
                newState,
                transition,
                cancelEffect != null ? new[] { cancelEffect } : Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Session 1924092e (2026-07-10) — false-positive guard for the esp-exit-variant
        /// <see cref="DeadlineNames.AdvisoryCompletion"/> window. The 1ec8f4c6 dead-end shape
        /// is "the ESP page closed normally and then NOTHING happens on the device". A reboot
        /// observed while that window is armed disproves the shape: the arming exit predates
        /// the reboot, so it was a pre-reboot page close (Device→Account handoff, autologon
        /// restart), not a final exit the user is now staring past. In session 1924092e the
        /// IME logged an AccountSetup phase line pre-sign-in (defaultuser0/AutoLogon frame),
        /// the handoff exit armed the window, and the deadline survived two reboots via state
        /// recovery — then failed the session mid-AccountSetup while apps were actively
        /// installing. Cancel the window; a later genuine guard-blocked post-AccountSetup
        /// exit re-arms a fresh one (the arming site is fire-once only while armed).
        /// <para>
        /// The advisory variant (<see cref="DecisionState.EspAdvisoryFailureRecordedUtc"/>
        /// set) is deliberately NOT cancelled: its anchor is a real ESP terminal failure and
        /// a reboot does not un-happen that failure — the window must still un-defang it.
        /// </para>
        /// <para>
        /// Trade-off: in a true dead-end where the user manually reboots inside the window,
        /// the verdict falls back to the max-lifetime watchdog (plus the backend timeout
        /// reconcile) instead of the 30-min resolution — acceptable against failing live
        /// enrollments, which is a trust-destroying false positive.
        /// </para>
        /// Returns the CancelDeadline effect (and cancels on <paramref name="builder"/>),
        /// or null when no esp-exit-variant window is armed.
        /// </summary>
        private static DecisionEffect? CancelEspExitVariantAdvisoryWindowOnReboot(
            DecisionState state,
            DecisionStateBuilder builder)
        {
            if (state.EspAdvisoryFailureRecordedUtc != null || !HasAdvisoryCompletionDeadline(state))
            {
                return null;
            }

            builder.CancelDeadline(DeadlineNames.AdvisoryCompletion);
            return new DecisionEffect(
                DecisionEffectKind.CancelDeadline,
                cancelDeadlineName: DeadlineNames.AdvisoryCompletion);
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

        /// <summary>
        /// Resolution window for a completion dead-end. 30 minutes is long enough for the user
        /// to dismiss/leave the ESP page, reach the desktop, and for IME's user-session
        /// processing to settle — and short enough to kill the multi-hour "InProgress until
        /// max-lifetime" tail. Two arming sites share it:
        /// <list type="bullet">
        ///   <item>the advisory-defang path (session 8bc1180f: Apps subcategory failed →
        ///         AccountSetupProvisioningComplete impossible, IME settle probe starved), and</item>
        ///   <item>the guard-blocked post-AccountSetup <c>esp_exiting</c> path (session
        ///         1ec8f4c6: ESP page closes normally — Shell-Core 62407, errorCode=0 — while
        ///         AccountSetup/Apps is still in_progress; no failure ever fires, same dead-end,
        ///         see <c>HandleEspExitingV1</c>).</item>
        /// </list>
        /// </summary>
        private static readonly TimeSpan s_advisoryCompletionWindow = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Arming-time baseline keys carried on the AdvisoryCompletion deadline's
        /// <see cref="ActiveDeadline.FiresPayload"/> (session 1924092e enforcement-progress
        /// guard). The baselines live on the deadline — not on <see cref="DecisionState"/>
        /// facts — because they are meaningful only relative to a specific arming: replay
        /// reconstructs them deterministically and the snapshot serializer round-trips the
        /// payload with the deadline. Deadlines armed by pre-fix agents lack the keys; the
        /// progress guard then reports no progress and the fire resolves exactly as before.
        /// </summary>
        private const string AdvisoryArmOrdinalKey = "armSignalOrdinal";

        /// <inheritdoc cref="AdvisoryArmOrdinalKey"/>
        private const string AdvisoryArmAppTerminalCountKey = "armAppTerminalCount";

        /// <summary>
        /// Build the <see cref="DeadlineNames.AdvisoryCompletion"/> resolution deadline for
        /// either arming site. Floored at AgentBootUtc so a replayed signal can't fire it
        /// immediately at boot. Carries the arming-time enforcement baselines so
        /// <see cref="HasEnforcementProgressSinceArming"/> can tell an idle dead-end from a
        /// still-progressing enrollment at fire time.
        /// </summary>
        internal static ActiveDeadline BuildAdvisoryCompletionDeadline(DecisionState state, DecisionSignal signal) =>
            new ActiveDeadline(
                name: DeadlineNames.AdvisoryCompletion,
                dueAtUtc: EffectiveDeadlineBase(state, signal).Add(s_advisoryCompletionWindow),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.AdvisoryCompletion,
                    [AdvisoryArmOrdinalKey] =
                        signal.SessionSignalOrdinal.ToString(CultureInfo.InvariantCulture),
                    [AdvisoryArmAppTerminalCountKey] =
                        (state.AppInstallFacts.CompletedCount + state.AppInstallFacts.FailedCount)
                            .ToString(CultureInfo.InvariantCulture),
                });

        /// <summary>True when <see cref="DeadlineNames.AdvisoryCompletion"/> is armed in <paramref name="state"/>.</summary>
        internal static bool HasAdvisoryCompletionDeadline(DecisionState state)
            => FindAdvisoryCompletionDeadline(state) != null;

        /// <summary>The armed <see cref="DeadlineNames.AdvisoryCompletion"/> deadline, or null.</summary>
        internal static ActiveDeadline? FindAdvisoryCompletionDeadline(DecisionState state)
        {
            foreach (var d in state.Deadlines)
            {
                if (d.Name == DeadlineNames.AdvisoryCompletion) return d;
            }
            return null;
        }

        /// <summary>
        /// Session 1924092e (2026-07-10) — true when the device demonstrably kept enforcing
        /// user-phase work AFTER the AdvisoryCompletion window was armed, i.e. the "page
        /// closed and nothing happens" dead-end shape (1ec8f4c6) is disproven:
        /// <list type="bullet">
        ///   <item>an app reached a terminal install state since arming
        ///         (<see cref="DecisionState.AppInstallFacts"/> terminal count grew past the
        ///         arming-time baseline), or</item>
        ///   <item>the ESP/IME re-asserted a user-phase enrollment phase
        ///         (<see cref="EnrollmentPhase.AccountSetup"/> / <see cref="EnrollmentPhase.AppsUser"/>)
        ///         with a signal ordinal newer than the arming.</item>
        /// </list>
        /// Missing baselines (deadline armed by a pre-fix agent, then recovered) report no
        /// progress so legacy windows resolve with the pre-fix semantics.
        /// </summary>
        private static bool HasEnforcementProgressSinceArming(DecisionState state, ActiveDeadline armedDeadline)
        {
            var payload = armedDeadline.FiresPayload;
            if (payload == null) return false;

            if (payload.TryGetValue(AdvisoryArmAppTerminalCountKey, out var rawCount)
                && int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var armAppTerminalCount))
            {
                var appTerminalNow = state.AppInstallFacts.CompletedCount + state.AppInstallFacts.FailedCount;
                if (appTerminalNow > armAppTerminalCount) return true;
            }

            if (payload.TryGetValue(AdvisoryArmOrdinalKey, out var rawOrdinal)
                && long.TryParse(rawOrdinal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var armOrdinal)
                && state.CurrentEnrollmentPhase != null
                && state.CurrentEnrollmentPhase.SourceSignalOrdinal > armOrdinal
                && (state.CurrentEnrollmentPhase.Value == EnrollmentPhase.AccountSetup
                    || state.CurrentEnrollmentPhase.Value == EnrollmentPhase.AppsUser))
            {
                return true;
            }

            return false;
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

            // Stage / Outcome deliberately untouched — monitoring continues. The session's
            // normal completion paths (Hello/Desktop, IME pattern, AccountSetup provisioning
            // complete) still drive it and win whenever they fire first.
            //
            // Session 8bc1180f (2026-06-12): additionally arm the AdvisoryCompletion deadline.
            // When the failure is AccountSetup/Apps, every normal completion path can be dead
            // (registry never flips, IME settle probe starved by a never-started app, Hello
            // disabled) and the session would otherwise stay InProgress until the max-lifetime
            // watchdog — which by design issues no session verdict. The deadline resolves the
            // session either way (see HandleAdvisoryCompletionDeadlineFired). AddDeadline
            // replaces by name, so an earlier esp_exiting arm is intentionally re-based on the
            // (later) failure instant — the freshest dead-end signal owns the window.
            var advisoryCompletion = BuildAdvisoryCompletionDeadline(state, signal);
            builder.AddDeadline(advisoryCompletion);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspTerminalFailure));

            var effects = new[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: advisoryCompletion),
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

        /// <summary>
        /// <see cref="DeadlineNames.AdvisoryCompletion"/> fired — the resolution window after a
        /// completion dead-end expired without one of the normal completion paths terminating
        /// the session. Two arming sites lead here: the advisory-defanged ESP terminal failure
        /// (session 8bc1180f) and the guard-blocked post-AccountSetup <c>esp_exiting</c>
        /// (session 1ec8f4c6 — ESP page closed normally while AccountSetup/Apps was still
        /// in_progress; no failure ever fired). Resolve the session now, either way:
        /// <list type="bullet">
        ///   <item>
        ///   <b>Complete</b> — when the real-user completion conjunction holds:
        ///   <see cref="DecisionState.DesktopArrivedUtc"/> is set (DAD-validated real user;
        ///   defaultuser0/SYSTEM are excluded at the detector), Hello is resolved or the policy
        ///   is explicitly disabled, AND <see cref="DecisionState.ImeUserSessionCompletedUtc"/>
        ///   is at-or-after <see cref="DecisionState.AccountSetupEnteredUtc"/>. The IME
        ///   timestamp gate kills defaultuser0 ghosts: an OOBE/technician IME session completes
        ///   in the pre-AccountSetup frame, so its timestamp can never satisfy the conjunction,
        ///   and in flows that never enter AccountSetup the anchor is missing entirely. The
        ///   conjunction deliberately REPLACES <c>ShouldTransitionToAwaitingHello</c>'s strong
        ///   registry gate — in both dead-end variants that gate is unsatisfiable by
        ///   construction, and IME's own user-session completion plus the real-user desktop is
        ///   the independent evidence that user-phase enforcement actually finished.
        ///   </item>
        ///   <item>
        ///   <b>Fail</b> — otherwise; the device had 30 minutes to produce completion evidence
        ///   and did not. The two arming variants fail with distinct context:
        ///   <i>advisory variant</i> — un-defang: reason stays <c>esp_terminal_failure</c> and
        ///   <see cref="DecisionState.LastFailureTrigger"/> is stamped <c>EspTerminalFailure</c>
        ///   so the <c>EnrollmentTerminationHandler</c> likely-stuck app promotion treats it
        ///   exactly like a direct ESP terminal failure (the failure cause IS the ESP failure,
        ///   the deadline merely lifted the defang).
        ///   <i>esp-exit variant</i> — no ESP failure ever existed, so claiming
        ///   <c>esp_terminal_failure</c> would be wrong: reason is
        ///   <c>esp_exit_without_completion_evidence</c> and LastFailureTrigger stays the
        ///   deadline (<c>DeadlineFired</c>), which keeps the likely-stuck promotion OFF — the
        ///   ESP never gave up on those apps, the page just closed.
        ///   </item>
        ///   <item>
        ///   <b>Re-arm</b> (esp-exit variant only; session 1924092e, 2026-07-10) — before
        ///   failing, check <see cref="HasEnforcementProgressSinceArming"/>: when apps kept
        ///   reaching terminal install states or the ESP re-asserted a user phase AFTER the
        ///   arming, the device is demonstrably still enrolling — failing it would be a false
        ///   positive on a live enrollment. Re-arm a fresh window (with updated baselines)
        ///   instead. Convergent: each re-arm demands NEW progress; a session that stalls
        ///   fails one window later, a session that finishes completes via the normal paths.
        ///   </item>
        /// </list>
        /// Stale-fire guards mirror <c>HandleRealmJoinTimeoutDeadlineFired</c>: a fire without
        /// any arming anchor (advisory recorded OR a post-AccountSetup final exit), without the
        /// deadline still armed, or while a Finalizing transition is already in flight
        /// dead-ends as bookkeeping.
        /// </summary>
        private DecisionStep HandleAdvisoryCompletionDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var armedDeadline = FindAdvisoryCompletionDeadline(state);
            var deadlineStillArmed = armedDeadline != null;

            var hasAdvisoryAnchor = state.EspAdvisoryFailureRecordedUtc != null;
            // Presence-only anchor (L5, delta review 2026-07-02). The esp-exit arming site only
            // runs when AccountSetupEnteredUtc is already set, so both facts being present is
            // the anchor; `deadlineStillArmed` below is what rejects stray timer fires. The old
            // additional `EspFinalExitUtc >= AccountSetupEnteredUtc` comparison rejected fires
            // whose arming exit carried a backfilled/out-of-order source timestamp — returning
            // the session to the idle-until-max-lifetime dead-end this deadline exists to close.
            // An intermediate Device→Account exit normally happens BEFORE AccountSetup entry,
            // never arms the window, and dead-ends on
            // `advisory_completion_stale_deadline_not_armed`. Session 1924092e (2026-07-10)
            // showed it CAN slip in when the IME logs an AccountSetup phase line pre-sign-in
            // (defaultuser0/AutoLogon frame) — that misfire is defused downstream by the
            // reboot cancel (CancelEspExitVariantAdvisoryWindowOnReboot) and the
            // enforcement-progress re-arm below, not by the anchor.
            var hasEspExitAnchor = state.EspFinalExitUtc != null
                && state.AccountSetupEnteredUtc != null;

            string? staleReason = null;
            if (!hasAdvisoryAnchor && !hasEspExitAnchor) staleReason = "advisory_completion_without_anchor";
            else if (!deadlineStillArmed) staleReason = "advisory_completion_stale_deadline_not_armed";
            else if (state.Stage == SessionStage.Finalizing) staleReason = "advisory_completion_finalizing_already_in_flight";

            if (staleReason != null)
            {
                var bookkept = BumpStepBookkeeping(state, signal);
                var staleTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: bookkept.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}",
                    deadEndReason: staleReason);
                return new DecisionStep(bookkept, staleTransition, Array.Empty<DecisionEffect>());
            }

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.AdvisoryCompletion);

            var desktopArrived = state.DesktopArrivedUtc != null;
            var helloSatisfied = state.HelloResolvedUtc != null || state.HelloPolicyEnabled?.Value == false;
            // Shared with ShouldTransitionToAwaitingHello's proactive arm C (session a4537c36).
            var imeUserSessionGenuine = IsImeUserSessionGenuine(state);

            if (desktopArrived && helloSatisfied && imeUserSessionGenuine)
            {
                if (state.HelloResolvedUtc == null)
                {
                    builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
                    builder.HelloOutcome = new SignalFact<string>("Skipped", signal.SessionSignalOrdinal);
                }

                // Dispose a still-armed HelloSafety scheduler timer (8b8d611d pattern) so it
                // cannot fire post-Completion and re-enter the completion path.
                var helloSafetyCancelEffect = BuildHelloSafetyCancelEffectIfArmed(state);
                if (helloSafetyCancelEffect != null)
                {
                    builder.CancelDeadline(DeadlineNames.HelloSafety);
                }

                return CompleteThroughFinalizingOrDefer(
                    state: state,
                    signal: signal,
                    preparedBuilder: builder,
                    nextStepIndex: nextStep,
                    trigger: $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}",
                    leadingEffects: helloSafetyCancelEffect != null
                        ? new[] { helloSafetyCancelEffect }
                        : null);
            }

            // Enforcement-progress guard (session 1924092e, 2026-07-10) — esp-exit variant
            // only. The arming exit can be a misclassified handoff (IME logs an AccountSetup
            // phase line pre-sign-in, then the Device→Account page close arms the window).
            // When user-phase enforcement demonstrably progressed since arming, the dead-end
            // shape is disproven: re-arm a fresh window with updated baselines instead of
            // failing a live enrollment mid-install. The advisory variant is exempt — its
            // anchor is a real ESP terminal failure that a busy IME does not un-happen.
            if (!hasAdvisoryAnchor && HasEnforcementProgressSinceArming(state, armedDeadline!))
            {
                var rearmedDeadline = BuildAdvisoryCompletionDeadline(state, signal);
                builder.AddDeadline(rearmedDeadline);

                var rearmedState = builder.Build();
                var rearmedTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}");

                // Deliberately bypasses BuildCompletionWaitingEffect's fingerprint dedupe:
                // the missing-prerequisites set is typically unchanged since arming, but the
                // re-based resolution due-time is exactly what an operator debugging the
                // session needs on the timeline. Bounded by construction — at most one event
                // per 30-min window.
                var rearmedEffects = new[]
                {
                    new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: rearmedDeadline),
                    new DecisionEffect(
                        DecisionEffectKind.EmitEventTimelineEntry,
                        parameters: new Dictionary<string, string>
                        {
                            ["eventType"] = SharedConstants.EventTypes.CompletionWaiting,
                            ["source"] = "DecisionEngine",
                            ["severity"] = "Info",
                            ["immediateUpload"] = "false",
                            ["message"] = "Completion resolution window re-armed: enforcement still active after the ESP page exit",
                            ["missingPrerequisites"] = string.Join(",", BuildMissingCompletionPrerequisites(state)),
                            ["trigger"] = $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}:EnforcementActive",
                            ["stage"] = state.Stage.ToString(),
                            ["resolutionDeadlineDueAtUtc"] = rearmedDeadline.DueAtUtc.ToString("o"),
                        }),
                };

                return new DecisionStep(rearmedState, rearmedTransition, rearmedEffects);
            }

            // Conjunction not met — the session is failed. The two arming variants carry
            // distinct context (see method doc): the advisory variant un-defangs the original
            // ESP failure; the esp-exit variant never had an ESP failure, so it gets its own
            // reason literal and keeps the likely-stuck app promotion off (LastFailureTrigger
            // stays the deadline, not EspTerminalFailure).
            string reason;
            Dictionary<string, string> parameters;
            if (hasAdvisoryAnchor)
            {
                // Registry-derived failure context (failureType/errorCode/...) lived on the
                // original signal and is already on the wire via the esp_failure_advisory
                // event; the terminal event carries the resolution reason instead.
                reason = "esp_terminal_failure";
                parameters = BuildEspFailureParameters(
                    signal: signal,
                    reason: reason,
                    advisoryPath: false,
                    observations: state.ScenarioObservations);
                parameters["advisoryReason"] = "advisory_completion_window_expired_without_completion_evidence";
                builder.WithLastFailureTrigger(nameof(DecisionSignalKind.EspTerminalFailure), signal.SessionSignalOrdinal);
            }
            else
            {
                // esp-exit variant: minimal bag — no ContinueAnyway "failure screen" hints,
                // there was no failure screen. The ESP page closed normally and the device
                // then produced no completion evidence for the whole window.
                reason = "esp_exit_without_completion_evidence";
                parameters = new Dictionary<string, string>
                {
                    ["eventType"] = SharedConstants.EventTypes.EnrollmentFailed,
                    ["reason"] = reason,
                    ["advisoryReason"] = "esp_exit_resolution_window_expired_without_completion_evidence",
                };
                builder.WithLastFailureTrigger(nameof(DecisionSignalKind.DeadlineFired), signal.SessionSignalOrdinal);
            }

            builder
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .ClearDeadlines();

            var failedState = builder.Build();
            var failedTransition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Failed,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}");

            var failedEffects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: parameters,
                    typedPayload: BuildEspFailureAuditTrail(
                        postState: failedState,
                        decidedStage: SessionStage.Failed,
                        trigger: $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}",
                        failureReason: reason,
                        parameters: parameters)),
            };

            return new DecisionStep(failedState, failedTransition, failedEffects);
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
                    : SharedConstants.EventTypes.EnrollmentFailed,
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
        /// split between pre-reboot and post-reboot collection. A reboot also cancels an
        /// esp-exit-variant AdvisoryCompletion window — see
        /// <see cref="CancelEspExitVariantAdvisoryWindowOnReboot"/>.
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

            var cancelEffect = CancelEspExitVariantAdvisoryWindowOnReboot(state, builder);

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

            var timelineEffect = new DecisionEffect(
                DecisionEffectKind.EmitEventTimelineEntry,
                parameters: timelineParams);
            var effects = cancelEffect != null
                ? new[] { cancelEffect, timelineEffect }
                : new[] { timelineEffect };

            return new DecisionStep(newState, transition, effects);
        }
    }
}
