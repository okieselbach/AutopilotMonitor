using System;
using System.Collections.Generic;
using System.Globalization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // RealmJoin (RJ) deployment-tracking handlers. Plan: tasks/zany-gathering-oasis plan.
    // The agent posts six DecisionSignalKinds when it observes the
    // HKLM\SYSTEM\CurrentControlSet\Services\realmjoin\Parameters key + HKLM\SOFTWARE\RealmJoin\Packages
    // (and the HKU\<sid>\... user-scope counterpart). Detection arms a 60-min timeout
    // and gates the enrollment-completion AND-gate. Resolution (phase 110), the
    // aborted-first-deployment release (phase left 100/101 for 200/210 without 110 —
    // session 224b2087) or the timeout releases the gate so the session can complete.
    // When the timeout fires while the first deployment is demonstrably still active
    // (phase 100/101 with deployment activity inside the last 60 min), the deadline is
    // re-armed instead — up to an absolute 4-h cap from detection (report 55e6afd61c9d:
    // a large package catalog outlived the 60-min window mid-install).
    public sealed partial class DecisionEngine
    {
        // 60-minute deadline from RJ-detected. Not configurable (per design choice —
        // the trigger is intentionally aggressive, the timeout has to bound it). Doubles
        // as the inactivity window for the activity-based extension: RJ package writes are
        // only observable at package COMPLETION (RJ creates the registry subkey when the
        // install finishes), so a single long install is registry-silent for its whole
        // duration and the window must not be tightened below the original 60 min.
        private static readonly TimeSpan s_realmJoinHardTimeout = TimeSpan.FromMinutes(60);

        // Absolute ceiling for activity-based extensions, measured from RJ detection.
        // Preserves the original design intent (the aggressive detection trigger stays
        // bounded) and sits safely inside AgentMaxLifetimeMinutes (360) — the agent's own
        // lifetime watchdog would otherwise be the only backstop.
        private static readonly TimeSpan s_realmJoinAbsoluteTimeout = TimeSpan.FromHours(4);

        // Payload keys consumed by the new handlers (set by the agent's RealmJoinWatcherAdapter).
        // Public because the V2.Core agent adapter assembles signal payloads with these keys.
        public static class RealmJoinPayloadKeys
        {
            public const string DeploymentPhase = "deploymentPhase";
            // Set on RealmJoinPhaseChanged: the phase the watcher observed immediately before
            // the transition. The reducer prefers the PERSISTED RealmJoinFacts.LastDeploymentPhase
            // (restart-safe) and only falls back to this process-local value.
            public const string PreviousPhase = "previousPhase";
            public const string PackageId = "packageId";
            public const string DisplayName = "displayName";
            public const string Version = "version";
            public const string Scope = "scope";          // "machine" | "user"
            public const string Success = "success";       // "true" | "false"
            public const string LastExitCode = "lastExitCode";
            // Set on RealmJoinDetected by the agent adapter from RealmJoin.exe's
            // file-version resource: bare version + release channel ("release"/"beta"/"canary"
            // — the SemVer prerelease tag, absent tag == stable release).
            public const string ProductVersion = "productVersion";
            public const string ReleaseChannel = "releaseChannel";
            // Set on RealmJoinAutoUpdateDetected: the version the facts carried before the
            // self-update (ProductVersion/ReleaseChannel then describe the NEW build).
            public const string PreviousVersion = "previousVersion";
        }

        /// <summary>
        /// Returns <c>true</c> when the RJ gate is OPEN — either RJ was never detected or it
        /// has already resolved / timed out (Outcome set). The Classic and SelfDeploying
        /// completion paths AND this with their existing predicates so an active RJ
        /// deployment blocks <see cref="TransitionToFinalizing"/> and the SelfDeploying
        /// terminal transition.
        /// </summary>
        internal static bool RealmJoinGateOpen(DecisionState state) =>
            RealmJoinGateOpen(state.RealmJoinFacts);

        /// <summary>
        /// Facts-level overload — lets the <c>completion_waiting</c> helper (liveness plan PR2)
        /// evaluate the gate against a <see cref="DecisionStateBuilder"/>'s in-flight facts
        /// before the new state is materialized.
        /// </summary>
        internal static bool RealmJoinGateOpen(RealmJoinFacts facts) =>
            facts.DetectedUtc == null
            || facts.ResolvedUtc != null
            || facts.Outcome != null;

        /// <summary>
        /// Build the RJ timeout deadline for an explicit due time. Initial arm: detection
        /// (floored at <see cref="DecisionState.AgentBootUtc"/> via <see cref="EffectiveDeadlineBase"/>
        /// so a replayed RealmJoinDetected signal cannot collapse the timer into immediate-fire
        /// at boot) + 60 min. Re-arm (activity-based extension): last activity + 60 min,
        /// capped at detection + 4 h.
        /// </summary>
        private static ActiveDeadline BuildRealmJoinTimeoutDeadline(DateTime dueAtUtc) =>
            new ActiveDeadline(
                name: DeadlineNames.RealmJoinTimeout,
                dueAtUtc: dueAtUtc,
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout,
                });

        /// <summary>
        /// First-observation handler for the RealmJoin Parameters registry key. Records
        /// <see cref="RealmJoinFacts.DetectedUtc"/>, captures the initial DeploymentPhase
        /// observation (if present in the payload), and arms the 60-min hard timeout.
        /// Idempotent — a second RealmJoinDetected signal in the same session is a bookkeeping
        /// no-op (the set-once helpers on <see cref="RealmJoinFacts"/> guard the writes).
        /// </summary>
        private DecisionStep HandleRealmJoinDetectedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            var alreadyDetected = state.RealmJoinFacts.DetectedUtc != null;

            var phase = TryReadPhase(signal);
            var productVersion = TryReadString(signal, RealmJoinPayloadKeys.ProductVersion);
            var releaseChannel = TryReadString(signal, RealmJoinPayloadKeys.ReleaseChannel);
            var updatedFacts = state.RealmJoinFacts.WithDetected(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            if (phase.HasValue)
            {
                updatedFacts = updatedFacts.WithLastPhase(phase.Value, signal.SessionSignalOrdinal);
            }
            if (!string.IsNullOrEmpty(productVersion))
            {
                updatedFacts = updatedFacts.WithProductVersion(productVersion!, signal.SessionSignalOrdinal);
            }
            if (!string.IsNullOrEmpty(releaseChannel))
            {
                updatedFacts = updatedFacts.WithReleaseChannel(releaseChannel!, signal.SessionSignalOrdinal);
            }
            builder.RealmJoinFacts = updatedFacts;

            // Replay path (agent restarted between the 101 and 200 observations): the watcher
            // re-fires Detected with the CURRENT phase and no PhaseChanged ever carries the
            // transition — the persisted LastDeploymentPhase is the only witness of the aborted
            // first deployment. Evaluate the abort rule here too so the gate does not stay
            // closed until the hard timeout.
            if (alreadyDetected
                && phase.HasValue
                && IsFirstDeploymentAbortTransition(state.RealmJoinFacts, state.RealmJoinFacts.LastDeploymentPhase?.Value, phase.Value))
            {
                return ReleaseFirstDeploymentIncomplete(
                    state: state,
                    signal: signal,
                    builder: builder,
                    nextStep: nextStep,
                    previousPhase: state.RealmJoinFacts.LastDeploymentPhase!.Value,
                    currentPhase: phase.Value,
                    triggerBase: nameof(DecisionSignalKind.RealmJoinDetected));
            }

            var effects = Array.Empty<DecisionEffect>();
            if (!alreadyDetected)
            {
                var deadline = BuildRealmJoinTimeoutDeadline(
                    EffectiveDeadlineBase(state, signal).Add(s_realmJoinHardTimeout));
                builder.AddDeadline(deadline);
                effects = new[]
                {
                    new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: deadline),
                };
            }

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.RealmJoinDetected));

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// RealmJoin reached <c>DeploymentPhase = CompletedFirstDeployment (110)</c>. Cancels
        /// the hard-timeout deadline (live scheduler + reducer view) and, when the other
        /// completion preconditions are already in, releases the deferred completion path:
        /// Classic → <see cref="TransitionToFinalizing"/>; SelfDeploying → direct
        /// <see cref="SessionStage.Completed"/> + <c>enrollment_complete</c>.
        /// </summary>
        private DecisionStep HandleRealmJoinResolvedV1(DecisionState state, DecisionSignal signal)
        {
            if (state.RealmJoinFacts.DetectedUtc == null)
            {
                // Defensive: Resolved without Detected. Record the resolution so the audit trail
                // captures the pre-existing-110 case (RJ already done before agent boot), but
                // skip the deadline-cancel since none was armed.
                var preBuilder = state.ToBuilder()
                    .WithStepIndex(state.StepIndex + 1)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
                preBuilder.RealmJoinFacts = state.RealmJoinFacts
                    .WithDetected(signal.OccurredAtUtc, signal.SessionSignalOrdinal)
                    .WithResolved(signal.OccurredAtUtc, 110, signal.SessionSignalOrdinal);
                var preState = preBuilder.Build();
                var preTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: preState.StepIndex,
                    trigger: nameof(DecisionSignalKind.RealmJoinResolved) + ":WithoutDetected");
                return new DecisionStep(preState, preTransition, Array.Empty<DecisionEffect>());
            }

            var nextStep = state.StepIndex + 1;
            var phase = TryReadPhase(signal) ?? 110;

            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.RealmJoinTimeout);
            builder.RealmJoinFacts = state.RealmJoinFacts.WithResolved(signal.OccurredAtUtc, phase, signal.SessionSignalOrdinal);

            var cancelEffect = BuildRealmJoinTimeoutCancelEffectIfArmed(state);

            return CompleteIfDeferredOrBookkeep(
                state: state,
                signal: signal,
                preparedBuilder: builder,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.RealmJoinResolved),
                leadingEffects: cancelEffect != null ? new[] { cancelEffect } : null);
        }

        /// <summary>
        /// 60-min hard-timeout fired without RJ reaching phase 110. Records
        /// <see cref="RealmJoinFacts.Outcome"/> = <c>"Timeout"</c>, emits a
        /// <c>realmjoin_timeout</c> timeline entry, and — when other completion preconditions
        /// are in — releases the deferred completion path.
        /// <para>
        /// <b>Idempotency</b>: a stale <see cref="DecisionSignalKind.DeadlineFired"/> signal can
        /// arrive after the deadline was cancelled by <see cref="HandleRealmJoinResolvedV1"/>
        /// (race between the cancel-effect reaching the scheduler and the timer firing). In
        /// that case any further work would emit a spurious <c>realmjoin_timeout</c> timeline
        /// event and re-enter <see cref="TransitionToFinalizing"/> — duplicating
        /// <c>phase_transition(FinalizingSetup)</c> on the wire. Bail out as a bookkept dead-end
        /// when either: (a) <see cref="RealmJoinFacts.Outcome"/> is already set (Resolved or
        /// Timeout) or (b) the <see cref="DeadlineNames.RealmJoinTimeout"/> deadline is no
        /// longer in the live state — both indicate the timer has been logically retired.
        /// </para>
        /// </summary>
        private DecisionStep HandleRealmJoinTimeoutDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var alreadyResolvedOrTimedOut = state.RealmJoinFacts.Outcome != null;
            ActiveDeadline? armedDeadline = null;
            foreach (var d in state.Deadlines)
            {
                if (d.Name == DeadlineNames.RealmJoinTimeout) { armedDeadline = d; break; }
            }

            // Third staleness shape (introduced with the activity-based extension): the fire
            // belongs to an OLDER deadline incarnation that a re-arm has since replaced —
            // recognizable because the armed deadline is due LATER than this fire
            // (OccurredAtUtc = DueAtUtc per the scheduler contract). Without this guard the
            // stale fire would evaluate the timeout/extension decision ahead of schedule.
            var supersededByRearm = armedDeadline != null && armedDeadline.DueAtUtc > signal.OccurredAtUtc;

            if (alreadyResolvedOrTimedOut || armedDeadline == null || supersededByRearm)
            {
                var bookkept = BumpStepBookkeeping(state, signal);
                var staleTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: bookkept.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.RealmJoinTimeout}",
                    deadEndReason: alreadyResolvedOrTimedOut
                        ? "realmjoin_timeout_stale_outcome_already_set"
                        : armedDeadline == null
                            ? "realmjoin_timeout_stale_deadline_not_armed"
                            : "realmjoin_timeout_stale_superseded_by_rearm");
                return new DecisionStep(bookkept, staleTransition, Array.Empty<DecisionEffect>());
            }

            var nextStep = state.StepIndex + 1;

            // Activity-based extension (report 55e6afd61c9d): when the first deployment is
            // demonstrably still active — phase 100/101 with deployment activity (phase change
            // or package observation) inside the last 60 min — re-arm instead of cutting off
            // mid-install. Bounded by an absolute 4-h cap from detection so the original
            // "aggressive trigger stays bounded" design intent survives. The idle case
            // (detected, but the first deployment never produced any activity) is unchanged:
            // LastActivityUtc stays null and the 60-min timeout fires exactly as before.
            var facts = state.RealmJoinFacts;
            var now = signal.OccurredAtUtc;
            var lastActivity = facts.LastActivityUtc?.Value;
            var detectedUtc = facts.DetectedUtc?.Value;
            var inFirstDeployment = facts.LastDeploymentPhase != null
                && IsRealmJoinFirstDeploymentPhase(facts.LastDeploymentPhase.Value);
            var recentActivity = lastActivity != null && now - lastActivity.Value < s_realmJoinHardTimeout;
            var withinCap = detectedUtc != null && now - detectedUtc.Value < s_realmJoinAbsoluteTimeout;

            if (inFirstDeployment && recentActivity && withinCap)
            {
                var windowDue = lastActivity!.Value.Add(s_realmJoinHardTimeout);
                var capDue = detectedUtc!.Value.Add(s_realmJoinAbsoluteTimeout);
                var newDue = windowDue < capDue ? windowDue : capDue;

                var rearm = BuildRealmJoinTimeoutDeadline(newDue);
                var rearmBuilder = state.ToBuilder()
                    .WithStepIndex(nextStep)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                    .CancelDeadline(DeadlineNames.RealmJoinTimeout)
                    .AddDeadline(rearm);

                var rearmState = rearmBuilder.Build();
                var rearmTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: $"DeadlineFired:{DeadlineNames.RealmJoinTimeout}:Extended");

                var rearmEffects = new[]
                {
                    new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: rearm),
                    BuildRealmJoinTimeoutExtendedEvent(facts, lastActivity.Value, newDue),
                };

                return new DecisionStep(rearmState, rearmTransition, rearmEffects);
            }

            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.RealmJoinTimeout);

            builder.RealmJoinFacts = state.RealmJoinFacts.WithTimeoutOutcome(signal.SessionSignalOrdinal);

            // Message differentiates WHY the gate gave up: absolute cap exhausted while still
            // active, activity went quiet, or the original hard timeout (no activity ever seen).
            var reason = inFirstDeployment && recentActivity && !withinCap
                ? RealmJoinTimeoutReasonAbsoluteCap
                : inFirstDeployment && lastActivity != null
                    ? RealmJoinTimeoutReasonInactivity
                    : RealmJoinTimeoutReasonHardTimeout;

            var timeoutEffect = BuildRealmJoinTimeoutEvent(state, reason);

            return CompleteIfDeferredOrBookkeep(
                state: state,
                signal: signal,
                preparedBuilder: builder,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.RealmJoinTimeout}",
                leadingEffects: new[] { timeoutEffect });
        }

        // RJ DeploymentPhase enum values relevant to the abort rule (mirrors
        // RealmJoin.Core.SoftwarePackaging.DeploymentPhase; the agent-side RealmJoinInfo
        // constants are not referencable from this netstandard shared lib).
        private const int RjPhaseRunningFirstDeployment = 100;
        private const int RjPhaseRunningFirstDeploymentAuto = 101;
        private const int RjPhaseCompletedFirstDeployment = 110;
        private const int RjPhaseRunningDeployment = 200;
        private const int RjPhaseCompletedDeployment = 210;

        private static bool IsRealmJoinFirstDeploymentPhase(int phase) =>
            phase == RjPhaseRunningFirstDeployment || phase == RjPhaseRunningFirstDeploymentAuto;

        /// <summary>
        /// The aborted-RJ-ESP shape (session 224b2087): the phase left the first-deployment
        /// window (100/101) for a regular deployment phase (200/210) — a transition that cannot
        /// occur in a healthy first deployment, whose only exit is CompletedFirstDeployment
        /// (110). Root cause observed in the RJ logs: an interactive logon lands seconds after
        /// 101, RJ reclassifies the run as a secondary-user deployment
        /// (isFirstMachineDeployment=False) and that branch writes 200/210, never 110.
        /// <para>
        /// Deliberately NOT triggered by 101 → 0 (RJ service restart may still complete the
        /// first deployment) nor by a first observation of 210 (RJ already completed before
        /// the agent booted — session 6f1959c0 starts 210 → 200; releasing there would be
        /// wrong). <paramref name="previousPhase"/> comes from the persisted
        /// <see cref="RealmJoinFacts.LastDeploymentPhase"/> where available so the rule
        /// survives an agent restart between the 101 and 200 observations.
        /// </para>
        /// </summary>
        private static bool IsFirstDeploymentAbortTransition(RealmJoinFacts facts, int? previousPhase, int currentPhase) =>
            facts.DetectedUtc != null
            && facts.Outcome == null
            && previousPhase.HasValue
            && IsRealmJoinFirstDeploymentPhase(previousPhase.Value)
            && (currentPhase == RjPhaseRunningDeployment || currentPhase == RjPhaseCompletedDeployment);

        /// <summary>
        /// Phase-transition observation from the watcher. Always persists the current phase
        /// into <see cref="RealmJoinFacts.LastDeploymentPhase"/> (restart-safe; also fixes the
        /// <c>realmjoin_timeout</c> event reporting "last phase: 0" regardless of how far RJ
        /// actually got). When <see cref="IsFirstDeploymentAbortTransition"/> matches, emits
        /// the <c>realmjoin_first_deployment_incomplete</c> Warning, cancels the hard-timeout
        /// deadline and releases the completion gate — for us RJ is finished at that point.
        /// </summary>
        /// <summary>
        /// RJ self-update observed after detection. Overrides the set-once version facts so
        /// <see cref="RealmJoinFacts.ProductVersion"/> / <see cref="RealmJoinFacts.ReleaseChannel"/>
        /// describe the build that actually ran the deployment. Pure bookkeeping: no gate, no
        /// deadline, no activity credit (the update precedes the deployment, it is not
        /// deployment progress). Missing <c>productVersion</c> dead-ends.
        /// </summary>
        private DecisionStep HandleRealmJoinAutoUpdateDetectedV1(DecisionState state, DecisionSignal signal)
        {
            var newVersion = TryReadString(signal, RealmJoinPayloadKeys.ProductVersion);
            if (string.IsNullOrEmpty(newVersion))
            {
                var deadEnd = BumpStepBookkeeping(state, signal);
                var deadEndTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: deadEnd.StepIndex,
                    trigger: nameof(DecisionSignalKind.RealmJoinAutoUpdateDetected),
                    deadEndReason: "realmjoin_autoupdate_missing_version");
                return new DecisionStep(deadEnd, deadEndTransition, Array.Empty<DecisionEffect>());
            }

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.RealmJoinFacts = state.RealmJoinFacts.WithVersionOverride(
                newVersion!,
                TryReadString(signal, RealmJoinPayloadKeys.ReleaseChannel),
                signal.SessionSignalOrdinal);

            var bookkept = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.RealmJoinAutoUpdateDetected));
            return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
        }

        private DecisionStep HandleRealmJoinPhaseChangedV1(DecisionState state, DecisionSignal signal)
        {
            var currentPhase = TryReadPhase(signal);
            if (currentPhase == null)
            {
                var deadEnd = BumpStepBookkeeping(state, signal);
                var deadEndTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: deadEnd.StepIndex,
                    trigger: nameof(DecisionSignalKind.RealmJoinPhaseChanged),
                    deadEndReason: "realmjoin_phase_changed_missing_phase");
                return new DecisionStep(deadEnd, deadEndTransition, Array.Empty<DecisionEffect>());
            }

            // Persisted fact first (survives agent restarts), watcher's process-local value
            // as fallback for the very first phase observation of a session.
            var previousPhase = state.RealmJoinFacts.LastDeploymentPhase?.Value
                ?? TryReadInt(signal, RealmJoinPayloadKeys.PreviousPhase);

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.RealmJoinFacts = state.RealmJoinFacts
                .WithLastPhase(currentPhase.Value, signal.SessionSignalOrdinal)
                .WithActivity(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            if (!IsFirstDeploymentAbortTransition(state.RealmJoinFacts, previousPhase, currentPhase.Value))
            {
                var bookkept = builder.Build();
                var transition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.RealmJoinPhaseChanged));
                return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
            }

            return ReleaseFirstDeploymentIncomplete(
                state: state,
                signal: signal,
                builder: builder,
                nextStep: nextStep,
                previousPhase: previousPhase!.Value,
                currentPhase: currentPhase.Value,
                triggerBase: nameof(DecisionSignalKind.RealmJoinPhaseChanged));
        }

        /// <summary>
        /// Shared gate-release for the aborted-first-deployment rule — called from
        /// <see cref="HandleRealmJoinPhaseChangedV1"/> and from the
        /// <see cref="HandleRealmJoinDetectedV1"/> replay path (agent restarted between the
        /// 101 and 200 observations: the watcher re-fires Detected with the CURRENT phase and
        /// no PhaseChanged ever carries the transition, so the persisted fact is the only
        /// witness). Mirrors <see cref="HandleRealmJoinResolvedV1"/>'s release shape with a
        /// Warning timeline entry ahead of the completion effects.
        /// </summary>
        private DecisionStep ReleaseFirstDeploymentIncomplete(
            DecisionState state,
            DecisionSignal signal,
            DecisionStateBuilder builder,
            int nextStep,
            int previousPhase,
            int currentPhase,
            string triggerBase)
        {
            builder.CancelDeadline(DeadlineNames.RealmJoinTimeout);
            builder.RealmJoinFacts = builder.RealmJoinFacts.WithFirstDeploymentIncomplete(
                signal.OccurredAtUtc, currentPhase, signal.SessionSignalOrdinal);

            var leadingEffects = new List<DecisionEffect>(capacity: 2);
            var cancelEffect = BuildRealmJoinTimeoutCancelEffectIfArmed(state);
            if (cancelEffect != null) leadingEffects.Add(cancelEffect);
            leadingEffects.Add(BuildRealmJoinFirstDeploymentIncompleteEvent(previousPhase, currentPhase));

            return CompleteIfDeferredOrBookkeep(
                state: state,
                signal: signal,
                preparedBuilder: builder,
                nextStepIndex: nextStep,
                trigger: triggerBase + ":FirstDeploymentIncomplete",
                leadingEffects: leadingEffects);
        }

        /// <summary>
        /// Build the <c>realmjoin_first_deployment_incomplete</c> Warning timeline entry.
        /// Reducer-owned (like <see cref="BuildRealmJoinTimeoutEvent"/>) — the agent adapter
        /// only dual-emits the neutral <c>realmjoin_phase_changed</c> observation; the abort
        /// interpretation lives here where the persisted facts are.
        /// </summary>
        private static DecisionEffect BuildRealmJoinFirstDeploymentIncompleteEvent(int previousPhase, int currentPhase)
        {
            return new DecisionEffect(
                kind: DecisionEffectKind.EmitEventTimelineEntry,
                parameters: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EventType] = SharedConstants.EventTypes.RealmJoinFirstDeploymentIncomplete,
                    [SignalPayloadKeys.Source] = "DecisionEngine",
                    [SignalPayloadKeys.Severity] = "Warning",
                    [SignalPayloadKeys.Message] =
                        $"RealmJoin left first deployment (phase {previousPhase} -> {currentPhase}) without CompletedFirstDeployment (110) — the RealmJoin ESP likely aborted. Treating the RealmJoin deployment as finished.",
                    ["previousPhase"] = previousPhase.ToString(CultureInfo.InvariantCulture),
                    ["deploymentPhase"] = currentPhase.ToString(CultureInfo.InvariantCulture),
                });
        }

        /// <summary>
        /// Per-package install start. Observation-only: appends a row to
        /// <see cref="RealmJoinFacts.Packages"/>. Stage unchanged, no effects (the agent
        /// adapter dual-emits an <see cref="DecisionSignalKind.InformationalEvent"/> for the
        /// UI timeline).
        /// </summary>
        private DecisionStep HandleRealmJoinPackageStartedV1(DecisionState state, DecisionSignal signal)
        {
            var packageId = TryReadString(signal, RealmJoinPayloadKeys.PackageId);
            if (string.IsNullOrEmpty(packageId))
            {
                var deadEnd = BumpStepBookkeeping(state, signal);
                var deadEndTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: deadEnd.StepIndex,
                    trigger: nameof(DecisionSignalKind.RealmJoinPackageStarted),
                    deadEndReason: "realmjoin_package_started_missing_packageId");
                return new DecisionStep(deadEnd, deadEndTransition, Array.Empty<DecisionEffect>());
            }

            var displayName = TryReadString(signal, RealmJoinPayloadKeys.DisplayName) ?? string.Empty;
            var version = TryReadString(signal, RealmJoinPayloadKeys.Version);
            var scope = TryReadString(signal, RealmJoinPayloadKeys.Scope) ?? RealmJoinPackageFact.ScopeMachine;

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.RealmJoinFacts = state.RealmJoinFacts
                .WithPackageStarted(
                    packageId: packageId!,
                    displayName: displayName,
                    version: version,
                    scope: scope,
                    startedUtc: signal.OccurredAtUtc)
                .WithActivity(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.RealmJoinPackageStarted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Per-package install terminal outcome. Updates the matching row in
        /// <see cref="RealmJoinFacts.Packages"/> with success / lastExitCode / completedUtc.
        /// Stage unchanged.
        /// </summary>
        private DecisionStep HandleRealmJoinPackageCompletedV1(DecisionState state, DecisionSignal signal)
        {
            var packageId = TryReadString(signal, RealmJoinPayloadKeys.PackageId);
            if (string.IsNullOrEmpty(packageId))
            {
                var deadEnd = BumpStepBookkeeping(state, signal);
                var deadEndTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: deadEnd.StepIndex,
                    trigger: nameof(DecisionSignalKind.RealmJoinPackageCompleted),
                    deadEndReason: "realmjoin_package_completed_missing_packageId");
                return new DecisionStep(deadEnd, deadEndTransition, Array.Empty<DecisionEffect>());
            }

            var displayName = TryReadString(signal, RealmJoinPayloadKeys.DisplayName) ?? string.Empty;
            var version = TryReadString(signal, RealmJoinPayloadKeys.Version);
            var scope = TryReadString(signal, RealmJoinPayloadKeys.Scope) ?? RealmJoinPackageFact.ScopeMachine;
            var success = TryReadBool(signal, RealmJoinPayloadKeys.Success) ?? false;
            var lastExitCode = TryReadInt(signal, RealmJoinPayloadKeys.LastExitCode) ?? 0;

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.RealmJoinFacts = state.RealmJoinFacts
                .WithPackageCompleted(
                    packageId: packageId!,
                    displayName: displayName,
                    version: version,
                    scope: scope,
                    completedUtc: signal.OccurredAtUtc,
                    success: success,
                    lastExitCode: lastExitCode)
                .WithActivity(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.RealmJoinPackageCompleted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        // ============================================================== internal helpers

        /// <summary>
        /// Shared completion-release helper used by <see cref="HandleRealmJoinResolvedV1"/> and
        /// <see cref="HandleRealmJoinTimeoutDeadlineFired"/>. Routes to one of three outcomes
        /// depending on which other completion preconditions are present:
        /// <list type="bullet">
        ///   <item><b>SelfDeploying deferred</b> (<see cref="RealmJoinFacts.SelfDeployingDeferredCompletion"/>):
        ///         direct <see cref="SessionStage.Completed"/> + <c>enrollment_complete</c>.</item>
        ///   <item><b>Classic both-resolved</b> (Hello + Desktop both in):
        ///         <see cref="TransitionToFinalizing"/>.</item>
        ///   <item><b>Neither</b>: bookkeeping only — the next Hello / Desktop / SelfDeploying
        ///         signal will trigger completion through the standard AND-gate (which now reads
        ///         the gate as open since <see cref="RealmJoinFacts.ResolvedUtc"/> or
        ///         <see cref="RealmJoinFacts.Outcome"/> is set).</item>
        /// </list>
        /// </summary>
        private DecisionStep CompleteIfDeferredOrBookkeep(
            DecisionState state,
            DecisionSignal signal,
            DecisionStateBuilder preparedBuilder,
            int nextStepIndex,
            string trigger,
            IReadOnlyList<DecisionEffect>? leadingEffects)
        {
            var selfDeployingDeferred = state.RealmJoinFacts.SelfDeployingDeferredCompletion?.Value == true;
            var classicReady = state.HelloResolvedUtc != null && state.DesktopArrivedUtc != null;

            if (selfDeployingDeferred)
            {
                // Plan v9 re-check guards: between the SelfDeploying-deadline-fire (which set
                // SelfDeployingDeferredCompletion) and now (RJ-resolve / RJ-timeout), the world
                // may have moved — AccountSetup may have arrived or a stronger Mode classification
                // (Classic/High, WhiteGlove/High) may have been set. In those cases the deferred
                // SelfDeploying terminal is no longer appropriate: clear the deferred flag, reset
                // the DeviceOnly hypothesis (would otherwise corrupt the WhiteGlove classifier),
                // and fall through to classicReady / bookkeeping evaluation.
                var monotonicModeConflict =
                    state.ScenarioProfile.Confidence == ProfileConfidence.High
                    && state.ScenarioProfile.Mode != EnrollmentMode.Unknown
                    && state.ScenarioProfile.Mode != EnrollmentMode.SelfDeploying;
                // Kiosk waiver (session 320b3bf7): on a registry-confirmed self-deploying
                // profile the AccountSetup entry is the IME false positive and must not
                // abort the deferred terminal — otherwise a hybrid+self-deploying session
                // would clear the deferred flag here and park forever. Same waiver as the
                // DeviceOnlyEspDetection sites (AccountSetupEntryVetoesSelfDeploying).
                var accountSetupEntered = AccountSetupEntryVetoesSelfDeploying(state);

                if (accountSetupEntered || monotonicModeConflict)
                {
                    // Clear on preparedBuilder.* (not state.*) — the caller has already written
                    // WithResolved(...) / WithTimeoutOutcome(...) into preparedBuilder, and using
                    // state.RealmJoinFacts as the base would discard that and leave
                    // RealmJoinGateOpen(postState) == false → session stuck (Plan v9 F1).
                    preparedBuilder.RealmJoinFacts = preparedBuilder.RealmJoinFacts.ClearSelfDeployingDeferred();
                    preparedBuilder.ClassifierOutcomes = preparedBuilder.ClassifierOutcomes.WithDeviceOnlyDeployment(
                        Hypothesis.UnknownInstance);
                    selfDeployingDeferred = false;
                    // fall through to classicReady / bookkeeping below
                }
                else
                {
                    // Promote ScenarioProfile to SelfDeploying/High via the monotonic-respecting
                    // updater (Plan v9 F2 — keeps state↔wire consistent; without this the
                    // enrollment_complete event would be emitted while the snapshot still showed
                    // ScenarioProfile.Mode=Unknown).
                    preparedBuilder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplySelfDeployingDeadlineConfirmed(
                        preparedBuilder.ScenarioProfile, signal);

                    preparedBuilder
                        .WithStage(SessionStage.Completed)
                        .WithOutcome(SessionOutcome.EnrollmentComplete)
                        .ClearDeadlines();
                    var completedState = preparedBuilder.Build();
                    var completedTransition = BuildTakenTransition(
                        before: state,
                        signal: signal,
                        toStage: SessionStage.Completed,
                        nextStepIndex: nextStepIndex,
                        trigger: trigger + ":SelfDeployingDeferred");

                    // Plan v9 Phase 4 — UI phase coverage: emit FinalizingSetup + Complete phase
                    // declarations BEFORE enrollment_complete so the Web timeline opens both bars
                    // for RJ-deferred-completion just like the direct SelfDeploying-terminal path.
                    var effects = new List<DecisionEffect>(capacity: (leadingEffects?.Count ?? 0) + 3);
                    if (leadingEffects != null && leadingEffects.Count > 0) effects.AddRange(leadingEffects);
                    effects.Add(BuildPhaseTransitionEffect(EnrollmentPhase.FinalizingSetup, completedState, trigger + ":SelfDeployingDeferred"));
                    effects.Add(BuildPhaseTransitionEffect(EnrollmentPhase.Complete, completedState, trigger + ":SelfDeployingDeferred"));
                    effects.Add(BuildEnrollmentCompleteEffect(completedState, trigger + ":SelfDeployingDeferred"));

                    return new DecisionStep(completedState, completedTransition, effects.ToArray());
                }
            }

            if (classicReady)
            {
                // This IS the gate-release path: the RealmJoin gate has just opened (the caller
                // wrote WithResolved / WithTimeoutOutcome / WithFirstDeploymentIncomplete into
                // preparedBuilder), so complete directly. Note the completion gates read `state`
                // (pre-resolution), where RJ is still closed — routing this through
                // CompleteThroughFinalizingOrDefer would re-defer on the very gate we just
                // released. A future second gate that must re-block here would re-check the
                // *post* state (WDP-v2 follow-up, ARCH-F1).
                return TransitionToFinalizing(
                    state: state,
                    signal: signal,
                    preparedBuilder: preparedBuilder,
                    nextStepIndex: nextStepIndex,
                    trigger: trigger,
                    extraLeadingEffects: leadingEffects);
            }

            // Bookkeeping only — defer to the next Hello / Desktop / SelfDeploying signal.
            var newState = preparedBuilder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStepIndex,
                trigger: trigger);

            return new DecisionStep(newState, transition, MaterializeEffects(leadingEffects));
        }

        /// <summary>
        /// Emit a scheduler-visible <see cref="DecisionEffectKind.CancelDeadline"/> for
        /// <see cref="DeadlineNames.RealmJoinTimeout"/> when it is actually armed in
        /// <paramref name="state"/>. Same pattern as <c>BuildHelloSafetyCancelEffectIfArmed</c>:
        /// avoids spurious scheduler noise on the cancel path when the timer was never armed.
        /// </summary>
        private static DecisionEffect? BuildRealmJoinTimeoutCancelEffectIfArmed(DecisionState state)
        {
            foreach (var d in state.Deadlines)
            {
                if (d.Name == DeadlineNames.RealmJoinTimeout)
                {
                    return new DecisionEffect(
                        DecisionEffectKind.CancelDeadline,
                        cancelDeadlineName: DeadlineNames.RealmJoinTimeout);
                }
            }
            return null;
        }

        // Reason strings carried in the realmjoin_timeout event payload ("reason") and
        // reflected in its message. HardTimeout = original semantics (no deployment activity
        // ever observed, 60 min from detection); Inactivity = the first deployment produced
        // activity at some point but went quiet for a full 60-min window; AbsoluteCap = still
        // active but the 4-h extension ceiling from detection is exhausted.
        internal const string RealmJoinTimeoutReasonHardTimeout = "hard_timeout";
        internal const string RealmJoinTimeoutReasonInactivity = "inactivity";
        internal const string RealmJoinTimeoutReasonAbsoluteCap = "absolute_cap";

        /// <summary>
        /// Build the <c>realmjoin_timeout</c> timeline-entry effect emitted by the deadline-
        /// fired handler. Unlike <see cref="DecisionSignalKind.RealmJoinDetected"/> /
        /// <see cref="DecisionSignalKind.RealmJoinResolved"/> (which the agent dual-emits as
        /// InformationalEvent), the timeout is a synthetic deadline so the reducer owns its
        /// timeline visibility.
        /// </summary>
        private static DecisionEffect BuildRealmJoinTimeoutEvent(DecisionState state, string reason)
        {
            var facts = state.RealmJoinFacts;
            var lastPhase = facts.LastDeploymentPhase?.Value ?? 0;
            var tracked = facts.Packages.Count;
            var completed = 0;
            for (var i = 0; i < facts.Packages.Count; i++)
            {
                if (facts.Packages[i].CompletedUtc != null) completed++;
            }

            var message = reason == RealmJoinTimeoutReasonAbsoluteCap
                ? $"RealmJoin did not reach phase 110 within {(int)s_realmJoinAbsoluteTimeout.TotalHours} h of detection (last phase: {lastPhase}) — monitoring window exhausted despite recent deployment activity."
                : reason == RealmJoinTimeoutReasonInactivity
                    ? $"RealmJoin did not reach phase 110 — no deployment activity for {(int)s_realmJoinHardTimeout.TotalMinutes} min (last phase: {lastPhase})."
                    : $"RealmJoin did not reach phase 110 within {(int)s_realmJoinHardTimeout.TotalMinutes} min (last phase: {lastPhase}).";

            return new DecisionEffect(
                kind: DecisionEffectKind.EmitEventTimelineEntry,
                parameters: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EventType] = SharedConstants.EventTypes.RealmJoinTimeout,
                    [SignalPayloadKeys.Source] = "DecisionEngine",
                    [SignalPayloadKeys.Severity] = "Warning",
                    [SignalPayloadKeys.Message] = message,
                    ["lastSeenPhase"] = lastPhase.ToString(CultureInfo.InvariantCulture),
                    ["packagesTracked"] = tracked.ToString(CultureInfo.InvariantCulture),
                    ["packagesCompleted"] = completed.ToString(CultureInfo.InvariantCulture),
                    ["reason"] = reason,
                });
        }

        /// <summary>
        /// Build the <c>realmjoin_timeout_extended</c> timeline-entry effect emitted when the
        /// timeout deadline fires but the first deployment is demonstrably still active and
        /// the monitoring window is re-armed instead. Reducer-owned like
        /// <see cref="BuildRealmJoinTimeoutEvent"/> (synthetic deadline, no agent dual-emit).
        /// </summary>
        private static DecisionEffect BuildRealmJoinTimeoutExtendedEvent(
            RealmJoinFacts facts,
            DateTime lastActivityUtc,
            DateTime newDueUtc)
        {
            var lastPhase = facts.LastDeploymentPhase?.Value ?? 0;
            var tracked = facts.Packages.Count;
            var completed = 0;
            for (var i = 0; i < facts.Packages.Count; i++)
            {
                if (facts.Packages[i].CompletedUtc != null) completed++;
            }

            return new DecisionEffect(
                kind: DecisionEffectKind.EmitEventTimelineEntry,
                parameters: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EventType] = SharedConstants.EventTypes.RealmJoinTimeoutExtended,
                    [SignalPayloadKeys.Source] = "DecisionEngine",
                    [SignalPayloadKeys.Severity] = "Info",
                    [SignalPayloadKeys.Message] =
                        $"RealmJoin first deployment still active (phase {lastPhase}, {completed}/{tracked} packages completed, last activity {lastActivityUtc:HH:mm:ss} UTC) — extending monitoring window to {newDueUtc:HH:mm:ss} UTC (cap {(int)s_realmJoinAbsoluteTimeout.TotalHours} h after detection).",
                    ["deploymentPhase"] = lastPhase.ToString(CultureInfo.InvariantCulture),
                    ["packagesTracked"] = tracked.ToString(CultureInfo.InvariantCulture),
                    ["packagesCompleted"] = completed.ToString(CultureInfo.InvariantCulture),
                    ["lastActivityUtc"] = lastActivityUtc.ToString("O", CultureInfo.InvariantCulture),
                    ["extendedUntilUtc"] = newDueUtc.ToString("O", CultureInfo.InvariantCulture),
                });
        }

        // ---- payload helpers -------------------------------------------------------------

        private static int? TryReadPhase(DecisionSignal signal)
        {
            if (signal.Payload == null) return null;
            if (!signal.Payload.TryGetValue(RealmJoinPayloadKeys.DeploymentPhase, out var raw)) return null;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var phase)) return phase;
            return null;
        }

        private static string? TryReadString(DecisionSignal signal, string key)
        {
            if (signal.Payload == null) return null;
            return signal.Payload.TryGetValue(key, out var v) ? v : null;
        }

        private static bool? TryReadBool(DecisionSignal signal, string key)
        {
            if (signal.Payload == null) return null;
            if (!signal.Payload.TryGetValue(key, out var raw)) return null;
            return bool.TryParse(raw, out var b) ? (bool?)b : null;
        }

        private static int? TryReadInt(DecisionSignal signal, string key)
        {
            if (signal.Payload == null) return null;
            if (!signal.Payload.TryGetValue(key, out var raw)) return null;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? (int?)v : null;
        }
    }
}
