using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Termination;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Server-side control-plane plumbing extracted from <see cref="Program"/>'s <c>RunAgent</c>:
    /// the <see cref="ServerActionDispatcher"/> factory (with its rotate_config /
    /// upload_diagnostics / terminate_session callbacks) and the
    /// <see cref="EnrollmentOrchestrator"/> response-handler wiring that translates backend
    /// control signals (DeviceKillSignal / AdminAction / Actions[]) into ServerActions.
    /// </summary>
    internal static class ServerControlPlane
    {
        /// <summary>
        /// Constructs the live <see cref="ServerActionDispatcher"/>. Must be called from inside
        /// <see cref="EnrollmentOrchestrator.Start"/>'s onIngressReady hook so the supplied
        /// <paramref name="post"/> and <paramref name="terminationHandler"/> are already
        /// non-null (single-rail refactor — plan §5.3).
        /// </summary>
        public static ServerActionDispatcher BuildDispatcher(
            AgentConfiguration agentConfig,
            EnrollmentOrchestrator orchestrator,
            EnrollmentTerminationHandler terminationHandler,
            RemoteConfigService remoteConfigService,
            DiagnosticsPackageService diagnosticsService,
            ManualResetEventSlim shutdown,
            ManualResetEventSlim shutdownComplete,
            InformationalEventPost post,
            AgentLogger logger)
        {
            return new ServerActionDispatcher(
                configuration: agentConfig,
                logger: logger,
                rotateConfigAsync: async () =>
                {
                    try { var _ = await remoteConfigService.FetchConfigAsync(); return true; }
                    catch (Exception ex) { logger.Warning($"ServerAction rotate_config failed: {ex.Message}"); return false; }
                },
                uploadDiagnosticsAsync: async (suffix) =>
                    await diagnosticsService.CreateAndUploadAsync(enrollmentSucceeded: false, fileNameSuffix: suffix),
                onTerminateRequested: action => OnTerminateRequested(
                    action, agentConfig, orchestrator, terminationHandler, shutdown, shutdownComplete, logger),
                post: post);
        }

        /// <summary>
        /// Routes <see cref="TelemetryUploadOrchestrator.ServerResponseReceived"/> backend control
        /// signals to the correct handler. Three distinct semantic paths, not one:
        /// <list type="bullet">
        ///   <item><b>DeviceKillSignal</b> — hard kill by administrator. Synthesises a
        ///     <c>terminate_session</c> <see cref="ServerAction"/> with
        ///     <c>forceSelfDestruct=true</c> and routes it through the dispatcher, overriding
        ///     local <c>SelfDestructOnComplete=false</c>.</item>
        ///   <item><b>AdminAction=Succeeded</b> — informational. Admin clicked Mark-Succeeded in
        ///     the portal. Emits a single <c>admin_marked_session</c> timeline entry and lets
        ///     the agent finish its own path.</item>
        ///   <item><b>AdminAction=Failed</b> — soft admin-driven shutdown. Calls the agent's own
        ///     <see cref="EnrollmentTerminationHandler.Handle"/> with <c>outcome=Failed</c>,
        ///     which runs the same analyzers / diagnostics / summary / self-destruct sequence
        ///     as a locally-detected failure. Self-destruct is governed by local
        ///     <c>SelfDestructOnComplete</c> — no forced override.</item>
        ///   <item><b>upload.Actions[]</b> — genuinely backend-queued <see cref="ServerAction"/>s
        ///     (<c>rotate_config</c>, <c>request_diagnostics</c>). Still routed through the
        ///     dispatcher because these are real actions, not admin-button echoes.</item>
        ///   <item><b>DeviceBlocked</b> — non-terminal upload pause. Transport already reacted in
        ///     <c>TelemetryUploadOrchestrator.ApplyControlSignals</c>; here we only log.</item>
        /// </list>
        /// <para>
        /// <b>Shutdown race guard</b>: if the agent has already finished its own termination
        /// sequence (<paramref name="shutdownComplete"/> set), the SignalIngress is disposed
        /// and any further emit would throw <see cref="InvalidOperationException"/>. Short-circuit
        /// up front rather than crash deep in the dispatcher / post stack.
        /// </para>
        /// </summary>
        public static void Wire(
            EnrollmentOrchestrator orchestrator,
            ServerActionDispatcher dispatcher,
            InformationalEventPost post,
            Func<EnrollmentTerminationHandler> terminationHandlerAccessor,
            AgentConfiguration agentConfig,
            ManualResetEventSlim shutdownComplete,
            AgentLogger logger)
        {
            orchestrator.Transport.ServerResponseReceived += (sender, upload) =>
            {
                if (shutdownComplete.IsSet)
                {
                    logger.Debug("ServerControlPlane.Wire: shutdown already complete — ignoring backend control signals on this response.");
                    return;
                }

                if (upload.DeviceKillSignal)
                {
                    logger.Warning("Backend signalled DeviceKillSignal — synthesising terminate_session (force self-destruct).");
                    var killAction = new ServerAction
                    {
                        Type = ServerActionTypes.TerminateSession,
                        Reason = "DeviceKillSignal from administrator",
                        QueuedAt = DateTime.UtcNow,
                        Params = new Dictionary<string, string>
                        {
                            { "forceSelfDestruct", "true" },
                            { "gracePeriodSeconds", "0" },
                            { "origin", "kill_signal" },
                        },
                    };
                    try { dispatcher.DispatchAsync(new List<ServerAction> { killAction }).GetAwaiter().GetResult(); }
                    catch (Exception ex) { logger.Error("ServerActionDispatcher.DispatchAsync (kill) threw during server-response wiring.", ex); }
                }
                else if (!string.IsNullOrEmpty(upload.AdminAction))
                {
                    Func<string> stageNameAccessor = () =>
                    {
                        try { return orchestrator.CurrentState?.Stage.ToString(); }
                        catch (InvalidOperationException) { return null; }
                    };
                    Action<EnrollmentTerminatedEventArgs> onAdminFailed = args =>
                    {
                        var handler = terminationHandlerAccessor();
                        if (handler == null)
                        {
                            logger.Warning("AdminAction=Failed received but terminationHandler not constructed yet — ignoring.");
                            return;
                        }
                        handler.Handle(sender: null, args: args);
                    };
                    HandleAdminAction(upload.AdminAction, stageNameAccessor, post, onAdminFailed, agentConfig, logger);
                }

                if (upload.Actions != null && upload.Actions.Count > 0)
                {
                    var explicitActions = new List<ServerAction>(upload.Actions);
                    try { dispatcher.DispatchAsync(explicitActions).GetAwaiter().GetResult(); }
                    catch (Exception ex) { logger.Error("ServerActionDispatcher.DispatchAsync (explicit actions) threw during server-response wiring.", ex); }
                }

                if (upload.DeviceBlocked)
                {
                    var until = upload.UnblockAt.HasValue ? $"until {upload.UnblockAt.Value:O}" : "indefinitely";
                    logger.Warning($"Backend signalled DeviceBlocked {until} — uploads paused, session remains alive.");
                }
            };
        }

        /// <summary>
        /// Soft admin-override handling. Portal Mark-Succeeded / Mark-Failed is an echo of an
        /// admin button click, not a ServerAction: it bypasses the dispatcher (no
        /// <c>server_action_received / _executed</c> noise) and flows straight into the agent's
        /// own timeline / termination pipeline.
        /// </summary>
        internal static void HandleAdminAction(
            string adminAction,
            Func<string> stageNameAccessor,
            InformationalEventPost post,
            Action<EnrollmentTerminatedEventArgs> onAdminFailed,
            AgentConfiguration agentConfig,
            AgentLogger logger)
        {
            var outcome = string.Equals(adminAction, "Succeeded", StringComparison.OrdinalIgnoreCase)
                ? EnrollmentTerminationOutcome.Succeeded
                : EnrollmentTerminationOutcome.Failed;

            logger.Warning($"Backend signalled AdminAction={adminAction} — soft admin-override (outcome={outcome}).");

            try
            {
                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = Constants.EventTypes.AdminMarkedSession,
                    Severity = outcome == EnrollmentTerminationOutcome.Succeeded ? EventSeverity.Info : EventSeverity.Warning,
                    Source = "WireTelemetryServerResponse",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Administrator marked session as {adminAction} via portal.",
                    Data = new Dictionary<string, object>
                    {
                        { "adminOutcome", adminAction ?? string.Empty },
                        { "origin", "admin_action" },
                    },
                    ImmediateUpload = true,
                });
            }
            catch (InvalidOperationException ex)
            {
                // Race: SignalIngress was stopped between the shutdownComplete check above and
                // this emit. Debug-only — the admin click will surface in the next session.
                logger.Debug($"admin_marked_session emit suppressed (ingress stopped): {ex.Message}");
            }
            catch (Exception ex)
            {
                logger.Warning($"admin_marked_session emit failed: {ex.Message}");
            }

            if (outcome != EnrollmentTerminationOutcome.Failed) return;
            if (onAdminFailed == null) return;

            string stageName = null;
            try { stageName = stageNameAccessor?.Invoke(); }
            catch { /* accessor faulted — leave stageName null */ }

            try
            {
                onAdminFailed(new EnrollmentTerminatedEventArgs(
                    reason: EnrollmentTerminationReason.DecisionTerminalStage,
                    outcome: EnrollmentTerminationOutcome.Failed,
                    stageName: stageName,
                    terminatedAtUtc: DateTime.UtcNow,
                    details: "Admin marked session as Failed via portal."));
            }
            catch (Exception ex)
            {
                logger.Error("onAdminFailed (AdminAction=Failed) threw.", ex);
            }
        }

        /// <summary>
        /// Maps a <c>terminate_session</c> ServerAction's <c>adminOutcome</c> param onto an
        /// <see cref="EnrollmentTerminationOutcome"/>. Portal Mark-Succeeded was previously
        /// hard-coded to <see cref="EnrollmentTerminationOutcome.Failed"/>, so operators saw
        /// failures in SummaryDialog + got diagnostics uploads fired even when they had
        /// manually marked a session succeeded (Codex Finding 2 fix).
        /// <para>
        /// Mapping:
        /// <list type="bullet">
        ///   <item><c>"Succeeded"</c> (case-insensitive) → <see cref="EnrollmentTerminationOutcome.Succeeded"/>.</item>
        ///   <item><c>"Failed"</c>, other non-empty values → <see cref="EnrollmentTerminationOutcome.Failed"/>.</item>
        ///   <item>Missing/null/empty → <see cref="EnrollmentTerminationOutcome.Failed"/> (default-failure-safe
        ///     for a <c>terminate_session</c> action with no explicit outcome — e.g. a kill-signal-driven
        ///     synthesis that only sets <c>origin=kill_signal</c>).</item>
        /// </list>
        /// </para>
        /// </summary>
        internal static EnrollmentTerminationOutcome MapAdminOutcome(IReadOnlyDictionary<string, string> parameters)
        {
            if (parameters == null) return EnrollmentTerminationOutcome.Failed;
            if (!parameters.TryGetValue("adminOutcome", out var value) || string.IsNullOrEmpty(value))
                return EnrollmentTerminationOutcome.Failed;

            return string.Equals(value, "Succeeded", StringComparison.OrdinalIgnoreCase)
                ? EnrollmentTerminationOutcome.Succeeded
                : EnrollmentTerminationOutcome.Failed;
        }

        // ============================================================ Private terminate-callback

        /// <summary>
        /// <c>terminate_session</c> ServerAction callback — invoked by the dispatcher when the
        /// backend (or a synthesised DeviceKillSignal) requests termination. Forwards the
        /// admin-driven outcome to <see cref="EnrollmentTerminationHandler.Handle"/> and then
        /// blocks the ingest dispatcher thread until the main-thread cleanup has run, so no
        /// further HTTP work happens before the process actually exits.
        /// </summary>
        private static Task OnTerminateRequested(
            ServerAction action,
            AgentConfiguration agentConfig,
            EnrollmentOrchestrator orchestrator,
            EnrollmentTerminationHandler terminationHandler,
            ManualResetEventSlim shutdown,
            ManualResetEventSlim shutdownComplete,
            AgentLogger logger)
        {
            // Codex Finding 1 (defensive) — re-entry short-circuit. If a nested drain (e.g.
            // orchestrator.Stop's terminal drain) parses a NEW ActionId out of a late
            // response, the dispatcher would otherwise call us again on a thread that is
            // ALREADY inside the shutdown sequence. That thread is responsible for setting
            // shutdownComplete, so waiting on it below would self-deadlock. ActionId dedup
            // in ServerActionDispatcher handles the same-id case; this handles pathological
            // different-id re-entries.
            if (shutdown.IsSet)
            {
                logger.Debug("Terminate callback: shutdown already in progress — short-circuiting re-entry.");
                return Task.CompletedTask;
            }

            var forceSelfDestruct = action?.Params != null
                && action.Params.TryGetValue("forceSelfDestruct", out var f)
                && string.Equals(f, "true", StringComparison.OrdinalIgnoreCase);
            if (forceSelfDestruct && !agentConfig.SelfDestructOnComplete)
            {
                logger.Warning("ServerAction terminate_session: forceSelfDestruct=true overrides SelfDestructOnComplete=false.");
                agentConfig.SelfDestructOnComplete = true;
            }

            // Codex Finding 2: forward adminOutcome from the ServerAction params so a portal
            // Mark-Succeeded really lands as Succeeded locally (was hard-coded to Failed
            // before, masquerading every admin override as an error in SummaryDialog +
            // firing spurious diagnostics uploads).
            var mappedOutcome = MapAdminOutcome(action?.Params);

            logger.Warning($"ServerAction terminate_session received (ruleId={action?.RuleId}, reason={action?.Reason}, outcome={mappedOutcome}) — invoking termination handler.");
            // Synthesise a Terminated event as if the kernel fired it.
            terminationHandler.Handle(
                sender: null,
                args: new EnrollmentTerminatedEventArgs(
                    reason: EnrollmentTerminationReason.DecisionTerminalStage,
                    outcome: mappedOutcome,
                    stageName: orchestrator.CurrentState?.Stage.ToString(),
                    terminatedAtUtc: DateTime.UtcNow,
                    details: $"Server-requested termination: ruleId={action?.RuleId}, reason={action?.Reason}"));

            // Plan §6.2 synchronous-shutdown — block the ingest dispatcher thread until the
            // main-thread cleanup (orchestrator.Stop, client disposal) has fully run. This
            // prevents the agent from issuing another ingest call after
            // terminationHandler.Handle returns but before the process actually exits, which
            // would give the backend another window to re-deliver terminate_session. The
            // dedup in ServerActionDispatcher already squelches those re-deliveries, but
            // waiting here keeps the ingest loop itself quiet. 60s bound covers the 10s
            // spool drain + cleanup-service launch + orchestrator stop; if it times out the
            // agent is misbehaving and we return anyway rather than deadlock the ingest
            // thread forever.
            //
            // Codex Finding 1: the deadlock that previously fired here came from
            // TelemetryUploadOrchestrator raising ServerResponseReceived WHILE holding its
            // _drainGuard. That is fixed at source — events are now raised AFTER guard
            // release, so orchestrator.Stop's terminal DrainAllAsync on the main thread can
            // acquire the guard and actually upload agent_shutting_down before we unblock.
            if (!shutdownComplete.Wait(TimeSpan.FromSeconds(60)))
            {
                logger.Warning("Terminate callback: shutdownComplete wait timed out after 60s — returning without confirmed shutdown.");
            }
            return Task.CompletedTask;
        }
    }
}
