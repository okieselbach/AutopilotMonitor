using System;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// The slice of a Sessions row that a non-terminal ingest batch reads: the counter merge
    /// returns it as the post-merge state (SignalR delta, control signals to the agent, IME
    /// pattern health, the shutdown close) and the signal-only upload reads it directly.
    /// Deliberately not a <see cref="SessionSummary"/>: the hot path reads the row projected to
    /// exactly these columns (read whole, the row's size — not the storage server — set the
    /// ingest latency), so a summary built from that read would carry silent defaults in every
    /// other field. Batches that need the whole row (terminal, status or WhiteGlove transition,
    /// diagnostics upload) read a full summary and derive this via <see cref="From"/>.
    /// </summary>
    public sealed class SessionIngestSnapshot
    {
        public SessionIngestSnapshot(
            SessionStatus status, int currentPhase, string currentPhaseDetail, string failureReason,
            int eventCount, int? durationSeconds, DateTime? completedAt, string? diagnosticsBlobName,
            bool isPreProvisioned, string? adminMarkedAction, string pendingActionsJson,
            string agentVersion, string imeAgentVersion)
        {
            Status = status;
            CurrentPhase = currentPhase;
            CurrentPhaseDetail = currentPhaseDetail;
            FailureReason = failureReason;
            EventCount = eventCount;
            DurationSeconds = durationSeconds;
            CompletedAt = completedAt;
            DiagnosticsBlobName = diagnosticsBlobName;
            IsPreProvisioned = isPreProvisioned;
            AdminMarkedAction = adminMarkedAction;
            PendingActionsJson = pendingActionsJson;
            AgentVersion = agentVersion;
            ImeAgentVersion = imeAgentVersion;
        }

        public SessionStatus Status { get; }
        public int CurrentPhase { get; }
        public string CurrentPhaseDetail { get; }
        public string FailureReason { get; }
        public int EventCount { get; }
        public int? DurationSeconds { get; }
        public DateTime? CompletedAt { get; }
        public string? DiagnosticsBlobName { get; }
        public bool IsPreProvisioned { get; }
        /// <summary>Portal-button signal to the agent (set only by the Mark-Succeeded/Failed functions).</summary>
        public string? AdminMarkedAction { get; }
        /// <summary>Queued ServerActions (JSON); empty when none are pending.</summary>
        public string PendingActionsJson { get; }
        public string AgentVersion { get; }
        public string ImeAgentVersion { get; }

        /// <summary>The same slice taken off a full summary (the paths that read the whole row).</summary>
        public static SessionIngestSnapshot From(SessionSummary session) => new SessionIngestSnapshot(
            session.Status, session.CurrentPhase, session.CurrentPhaseDetail, session.FailureReason,
            session.EventCount, session.DurationSeconds, session.CompletedAt, session.DiagnosticsBlobName,
            session.IsPreProvisioned, session.AdminMarkedAction, session.PendingActionsJson,
            session.AgentVersion, session.ImeAgentVersion);
    }
}
