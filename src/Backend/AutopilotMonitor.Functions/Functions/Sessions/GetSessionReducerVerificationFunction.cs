using System.Net;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// <c>GET /api/sessions/{sessionId}/reducer-verification</c> — admin/ops endpoint that
    /// runs structural + semantic verification on a session's persisted SignalLog +
    /// DecisionTransitions journal (Plan §M5). Not tenant-exposed: gated by
    /// <c>GlobalAdminOnly</c> in the <c>EndpointAccessPolicyCatalog</c>.
    /// <para>
    /// <b>Structural checks</b> — always run: ordinal contiguity, step-index contiguity,
    /// ReducerVersion drift, orphaned <c>SignalOrdinalRef</c>.
    /// </para>
    /// <para>
    /// <b>Semantic replay</b> (Codex follow-up #6) — folds the persisted signal stream
    /// through the live backend <c>DecisionEngine</c> via <c>SignalSerializer.Deserialize</c>
    /// and compares the produced transitions to the stored journal on the semantic fields
    /// (Trigger / FromStage / ToStage / Taken / DeadEndReason / StepIndex). Skipped
    /// automatically when the stored ReducerVersion differs, the signal stream has gaps,
    /// or deserialisation fails at the head — those are structural conditions where a
    /// replay would not be meaningful.
    /// </para>
    /// </summary>
    public class GetSessionReducerVerificationFunction
    {
        /// <summary>Hard caps — match the other M5 read endpoints.</summary>
        internal const int MaxSignalsToLoad = 5000;
        internal const int MaxTransitionsToLoad = 5000;

        private readonly ILogger<GetSessionReducerVerificationFunction> _logger;
        private readonly ISignalRepository _signalRepo;
        private readonly IDecisionTransitionRepository _transitionRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionReducerVerificationFunction(
            ILogger<GetSessionReducerVerificationFunction> logger,
            ISignalRepository signalRepo,
            IDecisionTransitionRepository transitionRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _signalRepo = signalRepo;
            _transitionRepo = transitionRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessionReducerVerification")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/reducer-verification")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
                return badRequest;
            }

            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
            _logger.LogInformation("{Prefix} GetSessionReducerVerification", sessionPrefix);

            try
            {
                // Cross-tenant scope resolved upfront (point-read; same pattern as the other
                // session detail reads). The GlobalReadOrAdmin route gate restricts callers to
                // platform scope, which is exactly what the helper's HasGlobalScope gate checks.
                var requestCtx = req.GetRequestContext();
                var tenantIdForLoad = await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId);

                var signals = await _signalRepo.QueryBySessionAsync(
                    tenantIdForLoad, sessionId, MaxSignalsToLoad);
                var transitions = await _transitionRepo.QueryBySessionAsync(
                    tenantIdForLoad, sessionId, MaxTransitionsToLoad);

                // Read the live reducer's version via a transient DecisionEngine instance.
                // The engine has no heavy construction cost (no DI, no state) so this is
                // cheaper than threading a singleton through.
                var currentReducerVersion = new DecisionEngine().ReducerVersion;

                var report = ReducerVerifier.Verify(
                    tenantIdForLoad, sessionId, signals, transitions, currentReducerVersion);

                return await req.OkAsync(new GetSessionReducerVerificationResponse
                {
                    Success = true,
                    Truncated = signals.Count >= MaxSignalsToLoad
                        || transitions.Count >= MaxTransitionsToLoad
                        || SignalQueryLimits.IsPayloadBudgetExhausted(signals, SignalQueryLimits.DefaultMaxTotalPayloadChars),
                    Report = report,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, $"Reducer verification for session '{sessionId}'");
            }
        }
    }
}
