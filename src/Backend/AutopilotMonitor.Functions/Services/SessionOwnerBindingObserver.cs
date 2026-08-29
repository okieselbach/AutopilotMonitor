using System;
using System.Linq;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// SESSION-OWNER-BINDING-SHADOW — the single side-effect carrier around
    /// <see cref="SessionOwnershipPolicy"/>. Every session-scoped agent write (register, telemetry
    /// ingest, error report) runs its already-loaded Sessions row and validation through
    /// <see cref="Observe"/>, which records the outcome and returns the decision; callers that own
    /// a write path then stamp the binding (register through <c>StoreSessionAsync</c>, ingest through
    /// <see cref="StampAsync"/>).
    /// <para>
    /// Three carriers, deliberately (same shape as CERT-TENANT-BINDING):
    /// the request-row dimension <c>SessionOwnerBinding</c> for every outcome (denominator — worker
    /// LogInformation never reaches App Insights), a Warning trace for every non-Match/non-Fresh
    /// outcome (numerator with detail), and a throttled <c>SessionOwnerMismatch</c> ops event for
    /// would-reject outcomes so operators can wire an alert rule. Nothing here rejects.
    /// </para>
    /// </summary>
    public sealed class SessionOwnerBindingObserver
    {
        private static readonly TimeSpan OpsEventThrottle = TimeSpan.FromHours(1);

        private readonly ILogger<SessionOwnerBindingObserver> _logger;
        private readonly OpsEventService _opsEvents;
        private readonly IMemoryCache _cache;
        private readonly ISessionRepository _sessionRepo;

        public SessionOwnerBindingObserver(
            ILogger<SessionOwnerBindingObserver> logger,
            OpsEventService opsEvents,
            IMemoryCache cache,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _opsEvents = opsEvents;
            _cache = cache;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// Evaluates and records the binding for one request. Never throws — an exception in the
        /// evaluation is our defect, not evidence of a foreign device, and yields a Match decision
        /// with no stamp.
        /// </summary>
        /// <param name="req">The request (for the FunctionContext item and the agent-version header).</param>
        /// <param name="tenantId">Validated tenant.</param>
        /// <param name="sessionId">Session named by the request.</param>
        /// <param name="existingRow">The Sessions row as already loaded by the caller (null when absent).</param>
        /// <param name="validation">Successful security validation of this request.</param>
        /// <param name="endpoint">Short label of the calling function for the trace/ops event.</param>
        public SessionOwnershipPolicy.Decision Observe(
            HttpRequestData req,
            string tenantId,
            string sessionId,
            TableEntity? existingRow,
            SecurityValidationResult validation,
            string endpoint)
        {
            try
            {
                var decision = SessionOwnershipPolicy.Evaluate(existingRow, validation, DateTime.UtcNow);

                var items = req.FunctionContext?.Items;
                if (items != null)
                    items[SessionOwnershipPolicy.RequestItemKey] = decision.Outcome;

                if (decision.Outcome == SessionOwnershipPolicy.Outcome.Match
                    || decision.Outcome == SessionOwnershipPolicy.Outcome.Fresh)
                    return decision;

                var callerKind = validation.IsBootstrapAuth ? SessionOwner.Kinds.Bootstrap : SessionOwner.Kinds.Cert;
                var ownerKind = existingRow == null ? "none" : (SessionOwnershipPolicy.FromRow(existingRow)?.Kind ?? "legacy");
                var agentVersion = req.Headers.Contains("X-Agent-Version")
                    ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault() ?? "n/a"
                    : "n/a";

                // Identities stay out of the message: thumbprints/codes are on the row and in the
                // rejection log of the validator; here the actionable fields are the shape of the
                // mismatch and whether the serial agrees (re-enroll-without-wipe reads as
                // MismatchCert + serialMatch=true).
                _logger.LogWarning(
                    "AgentSessionOwnerBinding outcome={Outcome} enforced={Enforced} wouldReject={WouldReject} "
                    + "tenant={TenantId} session={SessionId} callerKind={CallerKind} ownerKind={OwnerKind} "
                    + "serialMatch={SerialMatch} endpoint={Endpoint} ver={AgentVersion}",
                    decision.Outcome, false, decision.WouldReject, tenantId, sessionId, callerKind, ownerKind,
                    decision.SerialMatch, endpoint, agentVersion);

                if (decision.WouldReject)
                    RaiseOpsEventThrottled(tenantId, sessionId, decision, callerKind, ownerKind, agentVersion, endpoint);

                return decision;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AgentSessionOwnerBinding evaluation failed for tenant {TenantId} session {SessionId} - request allowed",
                    tenantId, sessionId);
                return new SessionOwnershipPolicy.Decision(SessionOwnershipPolicy.Outcome.Match, null, false);
            }
        }

        /// <summary>
        /// Writes the decision's owner onto the Sessions row from a path that does not otherwise
        /// replace the row (telemetry ingest: legacy claim and rebinds). No-op without an owner to
        /// stamp. Fail-soft — the binding is observational in stage 1 and must never cost a batch.
        /// </summary>
        public async Task StampAsync(string tenantId, string sessionId, SessionOwnershipPolicy.Decision decision)
        {
            if (decision.OwnerToStamp == null)
                return;

            try
            {
                await _sessionRepo.UpdateSessionOwnerAsync(tenantId, sessionId, decision.OwnerToStamp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AgentSessionOwnerBinding stamp failed for tenant {TenantId} session {SessionId} outcome {Outcome}",
                    tenantId, sessionId, decision.Outcome);
            }
        }

        private void RaiseOpsEventThrottled(
            string tenantId, string sessionId, SessionOwnershipPolicy.Decision decision,
            string callerKind, string ownerKind, string agentVersion, string endpoint)
        {
            // Ingest fires once per batch; one ops event per (session, outcome) per hour is the
            // signal operators need, the rest is in the Warning trace.
            var key = $"sob:{tenantId}:{sessionId}:{decision.Outcome}";
            if (_cache.TryGetValue(key, out _))
                return;
            _cache.Set(key, true, OpsEventThrottle);

            _ = _opsEvents.RecordSessionOwnerMismatchAsync(
                    tenantId, sessionId, decision.Outcome, callerKind, ownerKind, decision.SerialMatch, agentVersion, endpoint)
                .ContinueWith(
                    t => _logger.LogWarning(t.Exception?.InnerException, "SessionOwnerMismatch ops event failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
