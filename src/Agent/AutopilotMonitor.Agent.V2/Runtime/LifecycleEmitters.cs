using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Lifecycle event/signal emitters extracted from <see cref="Program"/>'s <c>RunAgent</c>:
    /// the InformationalEvent emits for <c>agent_started</c> / <c>agent_version_check</c> /
    /// <c>agent_unrestricted_mode_changed</c>; the post-startup decision signals
    /// (<see cref="DecisionSignalKind.SessionStarted"/>, <see cref="DecisionSignalKind.AdminPreemptionDetected"/>,
    /// <see cref="DecisionSignalKind.SystemRebootObserved"/>); and the two watchdog factory
    /// methods that close over a deferred <see cref="InformationalEventPost"/> accessor
    /// (max-lifetime + auth-failure threshold). Single-rail refactor (plan §5.1) — every
    /// emit flows through the same <c>InformationalEventPost</c> the orchestrator's
    /// onIngressReady hook constructs.
    /// </summary>
    internal static class LifecycleEmitters
    {
        // ============================================================ InformationalEvent emits

        /// <summary>
        /// V1 parity — fire-and-forget <c>agent_started</c> event emitted after
        /// <see cref="EnrollmentOrchestrator.Start"/>. Carries a snapshot of the boot classification
        /// and the tenant-influenced runtime knobs so dashboards can classify crash-loops,
        /// backend-rejected sessions and forced self-destruct runs. Phase stays
        /// <see cref="EnrollmentPhase.Unknown"/> — the event is NOT a phase declaration.
        /// </summary>
        public static void EmitAgentStarted(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            Program.PreviousExitSummary previousExit,
            string agentVersion,
            RemoteConfigService remoteConfigService,
            AgentLogger logger)
        {
            try
            {
                // configVersion + remoteConfigFetched expose whether the agent is running
                // with fresh tenant settings (Succeeded), a stale cache (FromCache), or
                // built-in defaults (UsedDefaults). Closes the historical blind spot —
                // prior to this, ConfigVersion=0 was only deducible by inferring from
                // the absence of tenant-controlled knobs in the DataJson.
                var outcome = remoteConfigService?.LastFetchOutcome ?? RemoteConfigFetchOutcome.NotAttempted;
                var configVersion = remoteConfigService?.CurrentConfig?.ConfigVersion ?? 0;

                var data = new Dictionary<string, object>
                {
                    { "agentVersion", agentVersion },
                    { "commandLineArgs", agentConfig.CommandLineArgs ?? string.Empty },
                    { "isBootstrapSession", agentConfig.UseBootstrapTokenAuth },
                    { "awaitEnrollment", agentConfig.AwaitEnrollment },
                    { "selfDestructOnComplete", agentConfig.SelfDestructOnComplete },
                    { "certAuth", !agentConfig.UseBootstrapTokenAuth },
                    { "agentMaxLifetimeMinutes", agentConfig.AgentMaxLifetimeMinutes },
                    { "diagnosticsUploadMode", agentConfig.DiagnosticsUploadMode ?? "Off" },
                    { "previousExitType", previousExit?.ExitType ?? "unknown" },
                    { "unrestrictedMode", agentConfig.UnrestrictedMode },
                    { "configVersion", configVersion },
                    { "remoteConfigFetched", outcome == RemoteConfigFetchOutcome.Succeeded },
                    { "remoteConfigOutcome", outcome.ToString() },
                    // WinRT OOBE state sampled at this start. Together with previousExitType
                    // this makes late starts provable: "completed" on a first_run means OOBE
                    // was already over when the agent arrived (e.g. IME queue blocked).
                    { "oobeStateAtAgentStart", OobeStateReader.Read() },
                };

                if (!string.IsNullOrEmpty(previousExit?.CrashExceptionType))
                    data["previousCrashException"] = previousExit.CrashExceptionType;

                if (previousExit?.LastBootUtc.HasValue == true)
                    data["previousBootUtc"] = previousExit.LastBootUtc.Value.ToString("o");

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = SharedConstants.EventTypes.AgentStarted,
                    Severity = EventSeverity.Info,
                    Source = "Agent",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Agent v{agentVersion} started (previousExit={previousExit?.ExitType ?? "unknown"}).",
                    Data = data,
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                logger.Warning($"agent_started emission failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Wire-visible counterpart to the local "Failed to fetch remote config" warning
        /// log: when the initial <see cref="RemoteConfigService.FetchConfigAsync"/> call
        /// fell back to cache or built-in defaults, emit a dedicated event so the backend
        /// timeline shows the agent didn't get fresh tenant config — without it, the only
        /// trace was a Warning in the local agent log (gone after a SelfDestruct cleanup).
        /// No-op when the fetch succeeded.
        /// </summary>
        public static void EmitRemoteConfigFetchFailedIfAny(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            RemoteConfigService remoteConfigService,
            AgentLogger logger)
        {
            try
            {
                var outcome = remoteConfigService?.LastFetchOutcome ?? RemoteConfigFetchOutcome.NotAttempted;
                if (outcome == RemoteConfigFetchOutcome.Succeeded || outcome == RemoteConfigFetchOutcome.NotAttempted)
                    return;

                var data = new Dictionary<string, object>
                {
                    { "outcome", outcome.ToString() },
                    { "attempts", remoteConfigService.LastFetchAttempts },
                    { "failureType", remoteConfigService.LastFetchFailureType ?? string.Empty },
                    { "failureMessage", remoteConfigService.LastFetchFailureMessage ?? string.Empty },
                };

                if (remoteConfigService.LastFetchAuthStatusCode.HasValue)
                    data["authStatusCode"] = remoteConfigService.LastFetchAuthStatusCode.Value;

                var msg = outcome == RemoteConfigFetchOutcome.FromCache
                    ? $"Remote config fetch failed after {remoteConfigService.LastFetchAttempts} attempt(s); using cached config."
                    : $"Remote config fetch failed after {remoteConfigService.LastFetchAttempts} attempt(s); running with built-in defaults (ConfigVersion=0).";

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = SharedConstants.EventTypes.RemoteConfigFetchFailed,
                    Severity = EventSeverity.Warning,
                    Source = "Agent",
                    Phase = EnrollmentPhase.Unknown,
                    Message = msg,
                    Data = data,
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                logger.Warning($"remote_config_fetch_failed emission failed: {ex.Message}");
            }
        }

        public static void EmitVersionCheckIfAny(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            AgentLogger logger)
        {
            try
            {
                var buildResult = VersionCheckEventBuilder.TryBuild(
                    sessionId: agentConfig.SessionId,
                    tenantId: agentConfig.TenantId,
                    agentStartTimeUtc: DateTime.UtcNow);

                if (!string.IsNullOrEmpty(buildResult?.ParseError))
                    logger.Warning($"VersionCheckEventBuilder parse error: {buildResult.ParseError}");

                if (buildResult?.Event != null)
                {
                    post.Emit(buildResult.Event);
                    logger.Info($"agent_version_check emitted (outcome={buildResult.Outcome}).");
                }
                else if (buildResult?.Deduped == true)
                {
                    logger.Debug($"agent_version_check deduped (outcome={buildResult.Outcome}).");
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"VersionCheckEventBuilder emission failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V1 parity — when <see cref="RemoteConfigMerger"/> flips the tenant-controlled
        /// <c>UnrestrictedMode</c> guardrail, surface the transition as an auditable event on
        /// the session timeline so operators can correlate subsequent gather-rule exec with the
        /// elevated policy. The V1 code lives in
        /// <c>MonitoringService.AuditUnrestrictedModeChange</c>.
        /// </summary>
        public static void EmitUnrestrictedModeAuditIfChanged(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            RemoteConfigMergeResult mergeResult,
            AgentLogger logger)
        {
            if (mergeResult == null || !mergeResult.UnrestrictedModeChanged) return;

            try
            {
                var newValue = mergeResult.NewUnrestrictedMode;
                logger.Warning(
                    $"UnrestrictedMode changed: {mergeResult.OldUnrestrictedMode} → {newValue}. Emitting audit event.");

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = SharedConstants.EventTypes.AgentUnrestrictedModeChanged,
                    Severity = newValue ? EventSeverity.Warning : EventSeverity.Info,
                    Source = "RemoteConfigMerger",
                    Phase = EnrollmentPhase.Unknown,
                    Message = newValue
                        ? "Agent unrestricted mode ENABLED — gather rules can now execute without AllowList checks"
                        : "Agent unrestricted mode disabled — gather rules revert to AllowList checks",
                    Data = new Dictionary<string, object>
                    {
                        { "oldValue", mergeResult.OldUnrestrictedMode },
                        { "newValue", newValue },
                        { "changedAtUtc", DateTime.UtcNow.ToString("o") },
                    },
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                logger.Warning($"EmitUnrestrictedModeAuditIfChanged: emission failed: {ex.Message}");
            }
        }

        // ================================================================ Decision-signal posts

        /// <summary>
        /// V2 parity — post a <see cref="DecisionSignalKind.SessionStarted"/> signal with the
        /// tenant-registered session metadata so the reducer's session-anchor handler runs.
        /// Without this the DecisionState.Stage stays at the initial value and subsequent
        /// raw signals (ESP / Hello) see an uninitialised session.
        /// </summary>
        public static void PostSessionStarted(
            ISignalIngressSink ingressSink,
            SessionRegistrationResult registrationResult,
            AgentConfiguration agentConfig,
            string agentVersion,
            AgentLogger logger)
        {
            try
            {
                // V2 race-fix (10c8e0bf debrief, 2026-04-26) — registry-derived facts
                // (enrollmentType + isHybridJoin) moved to PostEnrollmentFactsObserved
                // because the legacy SessionStarted handler had a Stage-Wache that
                // dropped the profile update whenever any other signal had already
                // advanced Stage past SessionStarted by the time the post landed.
                // SessionStarted now carries only lifecycle-anchor metadata.
                var payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["validatedBy"] = registrationResult.ValidatedBy.ToString(),
                    ["agentVersion"] = agentVersion,
                    ["isBootstrapSession"] = agentConfig.UseBootstrapTokenAuth ? "true" : "false",
                };

                var evidence = new Evidence(
                    kind: EvidenceKind.Synthetic,
                    identifier: "register_session_success",
                    summary: "Session registration handshake succeeded; posting SessionStarted anchor for reducer.");

                ingressSink.Post(
                    kind: DecisionSignalKind.SessionStarted,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "Program.RunAgent",
                    evidence: evidence,
                    payload: payload);

                logger.Debug($"SessionStarted signal posted (validatedBy={registrationResult.ValidatedBy}).");
            }
            catch (Exception ex)
            {
                logger.Warning($"SessionStarted post failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V2 race-fix (10c8e0bf debrief, 2026-04-26) — post a
        /// <see cref="DecisionSignalKind.EnrollmentFactsObserved"/> signal carrying the
        /// registry-derived enrollment facts (<c>enrollmentType</c> + <c>isHybridJoin</c>)
        /// so the reducer can seed <see cref="State.EnrollmentScenarioProfile"/> via the
        /// stage-agnostic <c>HandleEnrollmentFactsObservedV1</c> handler.
        /// <para>
        /// Posted immediately before <see cref="PostSessionStarted"/> so the Inspector
        /// timeline reads naturally (facts → anchor) — but the reducer correctness does
        /// not depend on the order, which is the whole point of the new signal.
        /// </para>
        /// <para>
        /// AwaitEnrollment-mode is transparently covered: the bootstrap layer
        /// (<c>AgentBootstrap</c>) blocks until the MDM certificate is present, so by
        /// the time this method runs the Autopilot policy registry is guaranteed to
        /// hold the final enrollment values — a single post per agent run is enough.
        /// </para>
        /// </summary>
        public static void PostEnrollmentFactsObserved(
            ISignalIngressSink ingressSink,
            AgentLogger logger)
        {
            try
            {
                var enrollmentType = EnrollmentRegistryDetector.DetectEnrollmentType();
                var isHybridJoin = EnrollmentRegistryDetector.DetectHybridJoin();
                var isSelfDeploying = EnrollmentRegistryDetector.DetectSelfDeployingProfile();

                var payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["enrollmentType"] = enrollmentType,
                    ["isHybridJoin"] = isHybridJoin ? "true" : "false",
                    ["isSelfDeployingProfile"] = isSelfDeploying ? "true" : "false",
                };

                var evidence = new Evidence(
                    kind: EvidenceKind.Synthetic,
                    identifier: "enrollment_registry_facts_read",
                    summary: "Registry-derived enrollment facts read for scenario-profile seeding.");

                ingressSink.Post(
                    kind: DecisionSignalKind.EnrollmentFactsObserved,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "Program.RunAgent",
                    evidence: evidence,
                    payload: payload);

                logger.Debug($"EnrollmentFactsObserved signal posted (enrollmentType={enrollmentType}, isHybridJoin={isHybridJoin}, isSelfDeployingProfile={isSelfDeploying}).");
            }
            catch (Exception ex)
            {
                logger.Warning($"EnrollmentFactsObserved post failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V2 parity — post <see cref="DecisionSignalKind.AdminPreemptionDetected"/> when the
        /// register-session response carried an <c>AdminAction</c>. The reducer transitions
        /// Stage to terminal and emits the enrollment_complete/_failed telemetry event; the
        /// orchestrator's DecisionStepProcessor then raises the Terminated event, which the
        /// subscribed termination handler picks up — no direct synthesis needed.
        /// </summary>
        public static void PostAdminPreemption(
            ISignalIngressSink ingressSink,
            SessionRegistrationResult registrationResult,
            AgentLogger logger)
        {
            try
            {
                var adminOutcome = registrationResult.AdminAction; // "Succeeded" | "Failed"
                logger.Warning(
                    $"=== ADMIN OVERRIDE on startup: session already marked as {adminOutcome} by administrator — posting AdminPreemptionDetected signal ===");

                ingressSink.Post(
                    kind: DecisionSignalKind.AdminPreemptionDetected,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "register_session_response",
                    evidence: new Evidence(
                        kind: EvidenceKind.Synthetic,
                        identifier: $"admin_preemption:{adminOutcome}",
                        summary: $"Operator marked session as {adminOutcome} via portal before agent startup."),
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["adminOutcome"] = adminOutcome,
                    });
            }
            catch (Exception ex)
            {
                logger.Warning($"AdminPreemptionDetected post failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V2 parity — post <see cref="DecisionSignalKind.SystemRebootObserved"/> when the prior
        /// agent process was terminated by an OS reboot. The reducer records the reboot fact
        /// (used by the WhiteGlove reboot-observed scoring weight, plan §2.4) and emits the
        /// <c>system_reboot_detected</c> telemetry event as a side effect.
        /// </summary>
        public static void PostSystemRebootObserved(
            ISignalIngressSink ingressSink,
            Program.PreviousExitSummary previousExit,
            AgentLogger logger)
        {
            try
            {
                var payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["previousExitType"] = previousExit?.ExitType ?? string.Empty,
                    ["lastBootUtc"] = previousExit?.LastBootUtc?.ToString("o") ?? string.Empty,
                };

                ingressSink.Post(
                    kind: DecisionSignalKind.SystemRebootObserved,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "Program.DetectPreviousExit",
                    evidence: new Evidence(
                        kind: EvidenceKind.Synthetic,
                        identifier: "previous_exit_reboot_kill",
                        summary: $"Prior agent process terminated by OS reboot (exitType={previousExit?.ExitType})."),
                    payload: payload);
            }
            catch (Exception ex)
            {
                logger.Warning($"SystemRebootObserved post failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Death-Rattle (Plan §B — Edge-Triggered State Snapshots, 2026-05-03). Emits a
        /// single <c>prior_run_died_with_state</c> event on the next agent run when the
        /// previous run exited uncleanly (<c>reboot_kill</c> / <c>hard_kill</c> /
        /// <c>exception_crash</c>) AND its last persisted snapshot is readable. The event
        /// carries the dying run's snapshot under <c>data["priorState"]</c> so post-mortem
        /// diagnosis can see what the agent last knew before death — without relying on
        /// client-side log uploads. Plan §A's anchor enrichment additionally attaches the
        /// FRESH run's <c>data["decisionState"]</c> automatically (this event type is on
        /// the lifecycle-anchor allowlist), giving operators both views side-by-side: the
        /// persisted-at-death snapshot vs. the post-recovery reconstructed engine state.
        /// </summary>
        public static void PostPriorRunDiedWithState(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            Program.PreviousExitSummary previousExit,
            DecisionState priorState,
            AgentLogger logger)
        {
            try
            {
                // Defensive fallback: PreviousExitSummary.ExitType is settable but not
                // initialised on the property itself. The caller's IsUncleanExit gate
                // already rules out null in practice, but we don't want a null on the
                // wire payload if a future refactor changes the gate.
                var exitType = previousExit?.ExitType ?? "unknown";

                // Dictionary<string, object> is fine here because all three values are
                // non-null through our gates: exitType is non-null via the fallback above,
                // LastBootUtc?.ToString() ?? string.Empty always yields a string, and
                // DecisionStateSnapshotBuilder.Build always returns a non-null dict (its
                // VALUES may be null, but the dict itself is not). If a future addition
                // places a directly-nullable scalar into Data, switch the type to
                // Dictionary<string, object?> to silence nullable warnings.
                var data = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["previousExitType"] = exitType,
                    ["lastBootUtc"] = previousExit?.LastBootUtc?.ToString("o") ?? string.Empty,
                    ["priorState"] = DecisionStateSnapshotBuilder.Build(priorState),
                };

                if (!string.IsNullOrEmpty(previousExit?.CrashExceptionType))
                    data["previousCrashException"] = previousExit.CrashExceptionType;

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = SharedConstants.EventTypes.PriorRunDiedWithState,
                    Severity = EventSeverity.Warning,
                    Source = "Program.DetectPreviousExit",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Prior agent run died (exitType={exitType}); attached last-known DecisionState (Stage={priorState.Stage}, StepIndex={priorState.StepIndex})",
                    Data = data,
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                logger.Warning($"prior_run_died_with_state emission failed: {ex.Message}");
            }
        }

        // ====================================================== Cross-path shutdown emit helper

        /// <summary>
        /// Outcome of <see cref="EmitAgentShuttingDownGapPath"/>. Drives the caller's
        /// gate-release decision: <see cref="NoPost"/> means no emit was attempted (ingress
        /// not yet wired), so the cross-path idempotency slot should be released for a later
        /// fallback to retry; <see cref="Success"/> and <see cref="EmitFailed"/> both consume
        /// the slot (we tried — either it landed, or the ingress is faulted and a retry won't
        /// help).
        /// </summary>
        public enum AgentShuttingDownEmitResult
        {
            Success,
            NoPost,
            EmitFailed,
        }

        /// <summary>
        /// Shutdown-gap closure (2026-05-15) — emits a single
        /// <c>agent_shutting_down</c> event from one of the non-Terminated exit paths the
        /// V2 runtime had previously left silent (Ctrl+C, ProcessExit, unhandled exception
        /// in <c>RunAgent</c>, finally cleanup without prior Terminated). The
        /// <see cref="EnrollmentTerminationHandler"/> remains the canonical emitter on the
        /// happy path; this helper is for the gap-closure paths only.
        /// <para>
        /// <b>Idempotency contract:</b> the caller (typically <c>AgentRuntimeHost</c>) owns a
        /// single shared <see cref="int"/> flag and uses
        /// <c>Interlocked.Exchange(ref flag, 1) == 0</c> to claim the emit slot before
        /// invoking this method. <see cref="EnrollmentTerminationHandler"/> participates in
        /// the same gate via its <c>tryClaimShutdownEvent</c> constructor parameter so
        /// double-emit cannot happen when, say, Ctrl+C races a real Terminated transition.
        /// </para>
        /// <para>
        /// <b>Gate-release (P2 fix 2026-05-15):</b> when this method returns
        /// <see cref="AgentShuttingDownEmitResult.NoPost"/> the caller MUST release the slot
        /// (Volatile.Write 0) so the next attempt (e.g. the finally fallback after
        /// onIngressReady fires) can still surface the event. <see cref="AgentShuttingDownEmitResult.EmitFailed"/>
        /// is treated as "we tried" — releasing on emit-thrown would risk producing
        /// duplicate events under racing paths once the ingress recovers.
        /// </para>
        /// </summary>
        /// <param name="post">Live <see cref="InformationalEventPost"/>. Null returns
        /// <see cref="AgentShuttingDownEmitResult.NoPost"/> without emitting.</param>
        /// <param name="agentConfig">Carries SessionId / TenantId / agent build version.</param>
        /// <param name="reason">Discriminator stored in <c>data["reason"]</c>. Allowed values:
        /// <c>ctrl_c</c>, <c>process_exit</c>, <c>unhandled_exception</c>,
        /// <c>runtime_host_exit</c>, <c>self_update_restart</c> (runtime hash-mismatch
        /// update routing through the graceful shutdown, session b9b92d89 hardening). Other
        /// reasons (auth_failure / decision_terminal / max_lifetime) are emitted by their
        /// own dedicated paths.</param>
        /// <param name="agentStartTimeUtc">Used to compute uptime for the event payload.</param>
        /// <param name="agentVersion">Build hash tag, mirrored from <c>agent_started</c>.</param>
        /// <param name="logger">For best-effort log on emit failure.</param>
        /// <param name="exceptionType">Optional .NET exception type, populated when the
        /// caller is the unhandled-exception path.</param>
        /// <param name="exceptionMessage">Optional .NET exception message (truncated to
        /// 500 chars on the wire to keep payload size bounded).</param>
        public static AgentShuttingDownEmitResult EmitAgentShuttingDownGapPath(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            string reason,
            DateTime agentStartTimeUtc,
            string agentVersion,
            AgentLogger logger,
            string exceptionType = null,
            string exceptionMessage = null)
        {
            if (post == null)
            {
                logger?.Warning($"agent_shutting_down (reason={reason}) suppressed — ingress not ready.");
                return AgentShuttingDownEmitResult.NoPost;
            }

            try
            {
                var uptimeMinutes = Math.Round((DateTime.UtcNow - agentStartTimeUtc).TotalMinutes, 1);

                var data = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "reason", reason ?? "unknown" },
                    { "outcome", "Unknown" },
                    { "stage", string.Empty },
                    { "uptimeMinutes", uptimeMinutes },
                    { "agentVersion", agentVersion ?? string.Empty },
                };

                if (!string.IsNullOrEmpty(exceptionType))
                    data["exceptionType"] = exceptionType;
                if (!string.IsNullOrEmpty(exceptionMessage))
                {
                    // Bound payload growth — exception messages can be long stack-trace text.
                    var trimmed = exceptionMessage.Length > 500
                        ? exceptionMessage.Substring(0, 500) + "…"
                        : exceptionMessage;
                    data["exceptionMessage"] = trimmed;
                }

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = SharedConstants.EventTypes.AgentShuttingDown,
                    Severity = string.Equals(reason, "unhandled_exception", StringComparison.Ordinal)
                        ? EventSeverity.Error
                        : EventSeverity.Info,
                    Source = "AgentRuntimeHost",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Agent shutting down (reason={reason}).",
                    Data = data,
                    ImmediateUpload = true,
                });
                return AgentShuttingDownEmitResult.Success;
            }
            catch (Exception ex)
            {
                logger?.Warning($"agent_shutting_down (reason={reason}) emission failed: {ex.Message}");
                return AgentShuttingDownEmitResult.EmitFailed;
            }
        }

        // ================================================================ Watchdog handlers

        /// <summary>
        /// V1 parity — when the kernel's max-lifetime watchdog fires, emit an explicit
        /// <c>enrollment_failed</c> event with <c>failureType=agent_timeout</c> BEFORE the
        /// regular termination path runs. Dashboards + KQL queries key on the event type +
        /// data dictionary to distinguish a genuine enrollment failure from a timeout shutdown.
        /// <para>
        /// The post is captured by accessor because the live <see cref="InformationalEventPost"/>
        /// is built inside the orchestrator's onIngressReady callback, AFTER subscription. The
        /// returned handler null-checks the accessor result: if the watchdog somehow fires
        /// before the ingress is up, we log-and-skip rather than crash.
        /// </para>
        /// </summary>
        public static EventHandler<EnrollmentTerminatedEventArgs> CreateMaxLifetimeEmitter(
            Func<InformationalEventPost> getLifecyclePost,
            AgentConfiguration agentConfig,
            DateTime agentStartTimeUtc,
            AgentLogger logger)
        {
            return (s, e) =>
            {
                if (e.Reason != EnrollmentTerminationReason.MaxLifetimeExceeded) return;
                var post = getLifecyclePost();
                if (post == null)
                {
                    logger.Warning("enrollment_failed (max_lifetime) suppressed — ingress not ready.");
                    return;
                }
                try
                {
                    var uptimeMin = (DateTime.UtcNow - agentStartTimeUtc).TotalMinutes;
                    // Phase stays Unknown per plan §1.4 phase-invariant — the UI timeline
                    // buckets chronologically into the last-declared phase. This fixes the
                    // legacy violation where enrollment_failed (max_lifetime) carried
                    // Phase=Complete and caused a phantom phase in the UI.
                    post.Emit(new EnrollmentEvent
                    {
                        SessionId = agentConfig.SessionId,
                        TenantId = agentConfig.TenantId,
                        EventType = SharedConstants.EventTypes.EnrollmentFailed,
                        Severity = EventSeverity.Error,
                        Source = "EnrollmentOrchestrator",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Agent max lifetime expired ({uptimeMin:F0} min) — enrollment did not complete in time",
                        Data = new Dictionary<string, object>
                        {
                            { "failureType", "agent_timeout" },
                            { "failureSource", "max_lifetime_timer" },
                            { "agentUptimeMinutes", Math.Round(uptimeMin, 1) },
                            { "agentMaxLifetimeMinutes", agentConfig.AgentMaxLifetimeMinutes },
                            { "stageAtTimeout", e.StageName ?? string.Empty },
                        },
                        ImmediateUpload = true,
                    });
                }
                catch (Exception emitEx)
                {
                    logger.Warning($"enrollment_failed (max_lifetime) emission failed: {emitEx.Message}");
                }
            };
        }

        /// <summary>
        /// Auth-failure watchdog: when MaxAuthFailures / AuthFailureTimeoutMinutes is exceeded
        /// the agent must shut down cleanly instead of hammering a backend that has definitely
        /// said no. Event fires at most once. V1 parity — emit a structured
        /// <c>agent_shutdown</c> event with reason=auth_failure and the full telemetry payload
        /// before tripping the shutdown signal so the backend sees WHY the agent terminated in
        /// the session timeline.
        /// </summary>
        public static EventHandler<AuthFailureThresholdEventArgs> CreateAuthThresholdHandler(
            Func<InformationalEventPost> getLifecyclePost,
            AgentConfiguration agentConfig,
            Action signalShutdown,
            AgentLogger logger,
            Func<bool> tryClaimShutdownEvent = null,
            Action releaseShutdownEventClaim = null)
        {
            return (s, e) =>
            {
                logger.Error($"Auth-failure threshold exceeded ({e.Reason}) — initiating shutdown.");

                // Cross-path shutdown gate (P1 fix 2026-05-15): auth-failure now participates
                // in the same idempotency gate as the other agent_shutting_down emitters so a
                // Terminated transition or a Ctrl+C racing this watchdog cannot produce two
                // events on the wire. When the host did not wire a gate (legacy callers / tests
                // that exercise the emit in isolation), the parameter is null = always-claim.
                bool gateClaimed = tryClaimShutdownEvent == null || tryClaimShutdownEvent();

                if (!gateClaimed)
                {
                    logger.Debug("agent_shutting_down (auth_failure) suppressed — gate already claimed by another path.");
                    signalShutdown();
                    return;
                }

                var post = getLifecyclePost();
                if (post != null)
                {
                    try
                    {
                        // Event-type unification (2026-05-15): the auth-failure path used to
                        // emit a separate `agent_shutdown` type (legacy V1 string). All V2
                        // shutdown paths now share `agent_shutting_down` with the failure
                        // class disambiguated via Data["reason"] — auth_failure here, others
                        // (decision_terminal / max_lifetime / ctrl_c / process_exit /
                        // unhandled_exception) in EnrollmentTerminationHandler + the new
                        // AgentRuntimeHost gap-closure emit paths.
                        post.Emit(new EnrollmentEvent
                        {
                            SessionId = agentConfig.SessionId,
                            TenantId = agentConfig.TenantId,
                            EventType = SharedConstants.EventTypes.AgentShuttingDown,
                            Severity = EventSeverity.Error,
                            Source = "AuthFailureTracker",
                            Phase = EnrollmentPhase.Unknown,
                            Message = $"Agent shut down after {e.ConsecutiveFailures} consecutive auth failures",
                            Data = new Dictionary<string, object>
                            {
                                { "reason", "auth_failure" },
                                { "consecutiveFailures", e.ConsecutiveFailures },
                                { "firstFailureTime", e.FirstFailureUtc.ToString("o") },
                                { "maxFailures", agentConfig.MaxAuthFailures },
                                { "timeoutMinutes", agentConfig.AuthFailureTimeoutMinutes },
                                { "lastOperation", e.LastOperation ?? string.Empty },
                                { "lastStatusCode", e.LastStatusCode },
                                { "thresholdReason", e.Reason ?? string.Empty },
                            },
                            ImmediateUpload = true,
                        });
                    }
                    catch (Exception emitEx)
                    {
                        logger.Warning($"agent_shutting_down (auth_failure) emission failed: {emitEx.Message}");
                        // post.Emit threw — we still consumed the gate slot (we did our best).
                        // No release: a later fallback can't sensibly retry an
                        // already-faulted ingress.
                    }
                }
                else
                {
                    // P2 fix: ingress is not ready yet, so no emit was actually attempted.
                    // Release the gate so the finally-block fallback (runtime_host_exit)
                    // can still surface the shutdown on the wire after we've signalled exit.
                    logger.Warning("agent_shutting_down (auth_failure) suppressed — ingress not ready.");
                    releaseShutdownEventClaim?.Invoke();
                }

                signalShutdown();
            };
        }
    }
}
