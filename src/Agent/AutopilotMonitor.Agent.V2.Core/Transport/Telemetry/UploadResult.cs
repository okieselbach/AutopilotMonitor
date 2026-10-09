#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Ergebnis eines Batch-Upload-Versuchs gegen das Backend. Plan §2.7a + §4.x M4.6.ε.
    /// <para>
    /// <b>M4.6.ε</b>: Carries optional backend-to-agent control signals that Legacy embedded in
    /// <c>IngestEventsResponse</c>: <see cref="DeviceBlocked"/> / <see cref="UnblockAt"/>
    /// (non-terminal quarantine), <see cref="DeviceKillSignal"/> (terminal force-self-destruct),
    /// <see cref="AdminAction"/> (portal-side Mark-Failed / Mark-Succeeded) and the generic
    /// <see cref="Actions"/> list. Program.cs subscribes to the transport's response-hook and
    /// feeds these through the <c>ServerActionDispatcher</c> / DeviceBlocked pause path.
    /// </para>
    /// </summary>
    public sealed class UploadResult
    {
        private UploadResult(
            bool success,
            string? errorReason,
            bool isTransient,
            bool deviceBlocked,
            DateTime? unblockAt,
            bool deviceKillSignal,
            string? adminAction,
            IReadOnlyList<ServerAction>? actions,
            bool isAuthFailure = false,
            bool requiresSplit = false,
            bool isPoison = false,
            IReadOnlyList<string>? poisonRowKeys = null,
            bool isSessionGone = false)
        {
            Success = success;
            ErrorReason = errorReason;
            IsTransient = isTransient;
            DeviceBlocked = deviceBlocked;
            UnblockAt = unblockAt;
            DeviceKillSignal = deviceKillSignal;
            AdminAction = adminAction;
            Actions = actions;
            IsAuthFailure = isAuthFailure;
            RequiresSplit = requiresSplit;
            IsPoison = isPoison;
            PoisonRowKeys = poisonRowKeys;
            IsSessionGone = isSessionGone;
        }

        public bool Success { get; }

        /// <summary>Null bei Success, gesetzt bei Fehler.</summary>
        public string? ErrorReason { get; }

        /// <summary>Bei Success ignoriert. Bei Fehler: true → Retry sinnvoll, false → dauerhafter Fehler (kein Retry).</summary>
        public bool IsTransient { get; }

        /// <summary>
        /// TRACE-H1: a 401/403 auth-permanent failure. The uploader already drove
        /// <c>AuthFailureTracker</c> → shutdown; the orchestrator retains the batch (cursor stays)
        /// and lets the auth path end the session.
        /// </summary>
        public bool IsAuthFailure { get; }

        /// <summary>
        /// TRACE-H1 (P1): a 413 "payload too large". NOT poison — the data is fine, only the batch
        /// size is wrong. The orchestrator splits/shrinks the batch and retries (never discards a
        /// multi-item batch); only a lone item that is still too large is quarantined, because that
        /// is the single case the agent can locally prove is permanently un-sendable.
        /// </summary>
        public bool RequiresSplit { get; }

        /// <summary>
        /// P1: the backend EXPLICITLY declared specific items permanently un-ingestable via a 4xx
        /// response body (<c>{ "poison": true, "rejectedRowKeys": [...] }</c>). This is the ONLY
        /// signal that authorises a discard — a bare status code never infers poison. The orchestrator
        /// drops exactly the named <see cref="PoisonRowKeys"/>, re-uploads the rest of the batch, then
        /// advances the cursor.
        /// </summary>
        public bool IsPoison { get; }

        /// <summary>The RowKeys the backend named as poison (item-level). Non-empty when <see cref="IsPoison"/>.</summary>
        public IReadOnlyList<string>? PoisonRowKeys { get; }

        /// <summary>
        /// HTTP 410: the backend no longer knows the session the batch belongs to (never
        /// registered, deleted, or being deleted). Not transient, not auth, not poison — retrying
        /// can never succeed. The orchestrator stops the drain (cursor stays) and raises
        /// <see cref="TelemetryUploadOrchestrator.SessionGone"/> when the batch belongs to the
        /// current session, or drops the batch's leading items when they are leftovers of a
        /// previous session id.
        /// </summary>
        public bool IsSessionGone { get; }

        /// <summary>
        /// Non-terminal quarantine signalled by the backend. The agent should stop draining
        /// uploads until <see cref="UnblockAt"/> (if set) elapses — the session stays alive so
        /// the Admin can un-block without discarding enrollment state. Plan §4.x M4.6.ε.
        /// </summary>
        public bool DeviceBlocked { get; }

        /// <summary>UTC time at which the <see cref="DeviceBlocked"/> quarantine auto-expires, or null for an indefinite block.</summary>
        public DateTime? UnblockAt { get; }

        /// <summary>
        /// Terminal kill-signal issued by an administrator. Legacy synthesised a
        /// <c>terminate_session</c> <see cref="ServerAction"/> with <c>forceSelfDestruct=true</c>
        /// and <c>gracePeriodSeconds=0</c>. V2 Program.cs replicates that synthesis + dispatches
        /// through the same termination path as the real decision-terminal.
        /// </summary>
        public bool DeviceKillSignal { get; }

        /// <summary>
        /// Portal-side Mark-Failed / Mark-Succeeded outcome string (e.g. <c>"failed"</c> /
        /// <c>"succeeded"</c>). Legacy synthesised a soft <c>terminate_session</c>
        /// <see cref="ServerAction"/> that respects the local <c>SelfDestructOnComplete</c> toggle.
        /// </summary>
        public string? AdminAction { get; }

        /// <summary>Generic backend-queued actions (<see cref="ServerAction"/>s) for this batch.</summary>
        public IReadOnlyList<ServerAction>? Actions { get; }

        public static UploadResult Ok() =>
            new UploadResult(true, null, isTransient: false,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null);

        /// <summary>
        /// Success with backend-to-agent control signals. Used by
        /// <see cref="BackendTelemetryUploader"/> when the 2xx response body carries one of the
        /// quarantine / kill / admin / actions fields.
        /// </summary>
        public static UploadResult OkWithSignals(
            bool deviceBlocked = false,
            DateTime? unblockAt = null,
            bool deviceKillSignal = false,
            string? adminAction = null,
            IReadOnlyList<ServerAction>? actions = null) =>
            new UploadResult(true, null, isTransient: false,
                deviceBlocked: deviceBlocked, unblockAt: unblockAt,
                deviceKillSignal: deviceKillSignal, adminAction: adminAction, actions: actions);

        public static UploadResult Transient(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: true,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null);

        public static UploadResult Permanent(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: false,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null);

        /// <summary>
        /// 401/403 auth-permanent failure. Like <see cref="Permanent"/> (no retry, batch retained)
        /// but flagged <see cref="IsAuthFailure"/> so the drain knows the auth-failure path
        /// (AuthFailureTracker → shutdown) owns the outcome.
        /// </summary>
        public static UploadResult Unauthorized(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: false,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null,
                isAuthFailure: true);

        /// <summary>
        /// 413 "payload too large". Non-retryable as-is, but the data is fine — flagged
        /// <see cref="RequiresSplit"/> so the drain shrinks the batch and retries instead of
        /// discarding (P1: never lose good telemetry to a size error).
        /// </summary>
        public static UploadResult TooLarge(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: false,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null,
                requiresSplit: true);

        /// <summary>
        /// Explicit item-level backend poison signal (P1). <paramref name="rejectedRowKeys"/> are the
        /// items the backend declared permanently un-ingestable; the orchestrator drops exactly those
        /// and re-uploads the rest of the batch.
        /// </summary>
        public static UploadResult Poison(IReadOnlyList<string> rejectedRowKeys, string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: false,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null,
                isPoison: true, poisonRowKeys: rejectedRowKeys ?? throw new ArgumentNullException(nameof(rejectedRowKeys)));

        /// <summary>
        /// HTTP 410 — the backend no longer knows the session. Flagged <see cref="IsSessionGone"/>;
        /// never retried, never split, never treated as poison.
        /// </summary>
        public static UploadResult SessionGone(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: false,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null,
                isSessionGone: true);
    }
}
