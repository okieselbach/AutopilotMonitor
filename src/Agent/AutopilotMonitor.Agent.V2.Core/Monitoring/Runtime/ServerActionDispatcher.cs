using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Executes server-issued actions delivered via ingest responses.
    ///
    /// Design notes:
    /// - Idempotent per type: each handler tolerates repeated delivery (at-least-once channel).
    /// - Unknown action types are logged as <c>server_action_unknown</c> and skipped — rolling out a new
    ///   type on the server must not break older agents.
    /// - Every action emits two telemetry events: <c>server_action_received</c> on entry and either
    ///   <c>server_action_executed</c> or <c>server_action_failed</c> on exit, so operators can audit the
    ///   end-to-end lifecycle from backend App Insights → agent session events.
    /// - All three concrete handlers are passed in as callbacks so the dispatcher itself does not
    ///   depend on the internal wiring of <see cref="RemoteConfigService"/>, <see cref="DiagnosticsPackageService"/>,
    ///   or the orchestrator's shutdown sequence.
    /// - ActionId dedup: repeated delivery of the same ActionId is observed in the wild when the
    ///   backend re-queues terminate_session before the agent has had a chance to acknowledge. The
    ///   dispatcher short-circuits duplicates before any telemetry fires, so the timeline shows a
    ///   single server_action_received / _executed pair per unique action (plan §6.2).
    /// </summary>
    public class ServerActionDispatcher
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly Func<Task<bool>> _rotateConfigAsync;
        private readonly Func<string, Task<DiagnosticsUploadResult>> _uploadDiagnosticsAsync;
        private readonly Func<ServerAction, Task> _onTerminateRequested;
        private readonly InformationalEventPost _post;
        private readonly ConcurrentDictionary<string, byte> _seenActionIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        /// <param name="rotateConfigAsync">
        /// Refetch remote config from the backend and apply to the agent's live state.
        /// Return true on success, false on failure (the handler will emit the appropriate telemetry).
        /// Typically wired to <c>RemoteConfigService.FetchConfigAsync</c>.
        /// </param>
        /// <param name="uploadDiagnosticsAsync">
        /// Trigger a diagnostics package upload with the given file-name suffix. Return the result
        /// (success/failure, blob name, error code). Typically wired to
        /// <c>DiagnosticsPackageService.CreateAndUploadAsync</c>.
        /// </param>
        /// <param name="onTerminateRequested">
        /// Called for <c>terminate_session</c> actions. The dispatcher does NOT execute the shutdown
        /// itself — shutdown requires coordinating timers, spool flush, and cleanup that lives in the
        /// orchestrator. The callback is responsible for the actual termination sequence.
        /// </param>
        /// <param name="post">
        /// Single-rail telemetry sink. Every emit flows through the signal ingress + reducer pass-through,
        /// never directly to <c>TelemetryEventEmitter</c> (plan §1 Invariant 1).
        /// </param>
        public ServerActionDispatcher(
            AgentConfiguration configuration,
            AgentLogger logger,
            Func<Task<bool>> rotateConfigAsync,
            Func<string, Task<DiagnosticsUploadResult>> uploadDiagnosticsAsync,
            Func<ServerAction, Task> onTerminateRequested,
            InformationalEventPost post)
        {
            _configuration = configuration;
            _logger = logger;
            _rotateConfigAsync = rotateConfigAsync;
            _uploadDiagnosticsAsync = uploadDiagnosticsAsync;
            _onTerminateRequested = onTerminateRequested;
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        /// <summary>
        /// Routes a batch of actions to their handlers. Processes sequentially — ordering is defined
        /// by the order actions were queued on the server. A failing handler does not abort the batch;
        /// subsequent actions still run so a bad rotate_config doesn't block a terminate_session.
        /// </summary>
        public virtual async Task DispatchAsync(List<ServerAction> actions)
        {
            if (actions == null || actions.Count == 0)
                return;

            foreach (var action in actions)
            {
                if (action == null) continue;

                // Action dedup — the backend's PendingActions queue delivers at-least-once, so the
                // same queued action (e.g. terminate_session) keeps arriving across ingest responses
                // until the agent actually exits. ServerAction has no ActionId field, so we compose
                // one from the fields the server fixes at queue time: Type + RuleId + QueuedAt. Every
                // re-delivery of the same server-queued action maps to the same key; two genuinely
                // distinct issuances (different RuleId or QueuedAt) get different keys and dispatch
                // independently. Squelch duplicates before any telemetry or handler side effect fires.
                var dedupKey = BuildDedupKey(action);
                if (!_seenActionIds.TryAdd(dedupKey, 0))
                {
                    _logger.Debug($"ServerActionDispatcher: duplicate action '{action.Type}' ({dedupKey}) — skipping");
                    continue;
                }

                EmitReceived(action);

                try
                {
                    switch (action.Type?.ToLowerInvariant())
                    {
                        case ServerActionTypes.RotateConfig:
                            await HandleRotateConfigAsync(action);
                            break;

                        case ServerActionTypes.RequestDiagnostics:
                            await HandleRequestDiagnosticsAsync(action);
                            break;

                        case ServerActionTypes.TerminateSession:
                            if (_onTerminateRequested != null)
                                await _onTerminateRequested(action);
                            else
                                EmitFailed(action, "no_terminate_handler_wired");
                            break;

                        default:
                            _logger.Warning($"ServerActionDispatcher: unknown action type '{action.Type}' — skipping");
                            EmitFailed(action, "unknown_action_type");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"ServerActionDispatcher: handler for '{action.Type}' threw", ex);
                    EmitFailed(action, ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private async Task HandleRotateConfigAsync(ServerAction action)
        {
            if (_rotateConfigAsync == null)
            {
                EmitFailed(action, "no_rotate_config_handler");
                return;
            }

            _logger.Info($"ServerAction rotate_config: refetching remote config (reason: {action.Reason})");
            var ok = await _rotateConfigAsync();
            if (ok)
                EmitExecuted(action);
            else
                EmitFailed(action, "config_fetch_failed");
        }

        private async Task HandleRequestDiagnosticsAsync(ServerAction action)
        {
            if (_uploadDiagnosticsAsync == null)
            {
                EmitFailed(action, "no_diagnostics_handler");
                return;
            }

            _logger.Info($"ServerAction request_diagnostics: initiating best-effort upload (reason: {action.Reason})");
            var result = await _uploadDiagnosticsAsync("server-requested");
            if (result != null && result.Success)
            {
                EmitExecuted(action, extraData: new Dictionary<string, object>
                {
                    { "blobName", result.BlobName ?? string.Empty }
                });
            }
            else
            {
                EmitFailed(action, result?.ErrorCode ?? "diagnostics_upload_failed");
            }
        }

        // ---- Telemetry helpers ----

        private void EmitReceived(ServerAction action)
        {
            PostEvent(
                eventType: Constants.EventTypes.ServerActionReceived,
                severity: EventSeverity.Info,
                message: $"Received server action '{action.Type}'",
                data: BuildTelemetryData(action));
        }

        private void EmitExecuted(ServerAction action, Dictionary<string, object> extraData = null)
        {
            var data = BuildTelemetryData(action);
            if (extraData != null)
            {
                foreach (var kvp in extraData)
                    data[kvp.Key] = kvp.Value;
            }

            PostEvent(
                eventType: Constants.EventTypes.ServerActionExecuted,
                severity: EventSeverity.Info,
                message: $"Executed server action '{action.Type}'",
                data: data);
        }

        private void EmitFailed(ServerAction action, string reason)
        {
            var data = BuildTelemetryData(action);
            data["failureReason"] = reason ?? string.Empty;

            PostEvent(
                eventType: Constants.EventTypes.ServerActionFailed,
                severity: EventSeverity.Warning,
                message: $"Failed to execute server action '{action.Type}': {reason}",
                data: data);
        }

        private void PostEvent(string eventType, EventSeverity severity, string message, Dictionary<string, object> data)
        {
            // Go through the InformationalEventPost.Emit(EnrollmentEvent) overload so the wire shape
            // stays identical to the legacy Action<EnrollmentEvent> path: the emitter stringifies the
            // data dictionary preserving invariant-culture formatting.
            try
            {
                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = eventType,
                    Severity = severity,
                    Source = "ServerActionDispatcher",
                    Phase = EnrollmentPhase.Unknown,
                    Message = message,
                    Timestamp = DateTime.UtcNow,
                    Data = data,
                });
            }
            catch (InvalidOperationException ex)
            {
                // Race: a terminal-drain response arrived after SignalIngress.Stop completed but
                // before TelemetryUploadOrchestrator.DrainAllAsync finished, so the ingress is
                // disposed. Swallowing this keeps the dispatch loop alive for any follow-up
                // actions in the same batch and prevents a nested Emit from corrupting the
                // outer termination sequence. Dropped events are acceptable at this point —
                // the session is being torn down.
                _logger.Debug($"ServerActionDispatcher: event '{eventType}' emit suppressed (ingress stopped): {ex.Message}");
            }
        }

        private static Dictionary<string, object> BuildTelemetryData(ServerAction action)
        {
            return new Dictionary<string, object>
            {
                { "actionType", action?.Type ?? string.Empty },
                { "reason", action?.Reason ?? string.Empty },
                { "ruleId", action?.RuleId ?? string.Empty },
                { "queuedAt", action?.QueuedAt.ToString("O") ?? string.Empty }
            };
        }

        private static string BuildDedupKey(ServerAction action)
        {
            // Composite of server-queue-time fields. QueuedAt is stamped when the backend enqueues
            // and stays fixed across re-deliveries; RuleId is set for rule-triggered actions and
            // null for admin/manual triggers. Type alone would be too aggressive (two genuine
            // terminate_session issuances from different admins would collide).
            return string.Concat(
                action.Type ?? string.Empty,
                "|",
                action.RuleId ?? string.Empty,
                "|",
                action.QueuedAt.ToString("O"));
        }
    }
}
