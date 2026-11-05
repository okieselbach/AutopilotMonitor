using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Per-instance cap on rule work a caller starts synchronously: the analyze dry-run, the manual
/// re-analysis and the logparser pattern test (D-379, D-410), each its own singleton subclass. The
/// work runs caller-authored patterns on the shared app, so the request rate must not decide how
/// many threads it holds. A caller who finds every slot taken is answered 429 at once with a retry
/// hint; the first refusal per instance, gate and hour raises <c>RuleEvaluationLimitReached</c> for
/// the operators, later refusals in that window stay in the Warning trace. Legitimate use never
/// fills a gate, so a refusal is load aimed at it.
/// </summary>
public abstract class RuleWorkGate : ConcurrencyGate
{
    internal static readonly TimeSpan AlertWindow = TimeSpan.FromHours(1);

    private readonly string _gate;
    private readonly string _busyMessage;
    private readonly int _retryAfterSeconds;
    private readonly OpsEventService _opsEvents;
    private readonly ILogger _logger;

    protected RuleWorkGate(int limit, string gate, string busyMessage, int retryAfterSeconds, OpsEventService opsEvents, ILogger logger)
        : base(limit)
    {
        _gate = gate;
        _busyMessage = busyMessage;
        _retryAfterSeconds = retryAfterSeconds;
        _opsEvents = opsEvents;
        _logger = logger;
    }

    /// <summary>
    /// The 429 for a caller who found every slot taken. The message names the situation only;
    /// clients phrase the retry from <c>Retry-After</c> (the portal appends "Try again in N s.").
    /// </summary>
    public async Task<HttpResponseData> RefuseAsync(HttpRequestData req, string? tenantId, string? upn)
    {
        _logger.LogWarning(
            "Rule work refused: the {Gate} gate has {Limit} evaluations running on this instance (tenant {TenantId})",
            _gate, Limit, tenantId);

        if (TakeAlert(DateTime.UtcNow, AlertWindow))
            await _opsEvents.RecordRuleEvaluationLimitReachedAsync(_gate, Limit, AlertWindow, tenantId, upn);

        return await req.ErrorAsync(HttpStatusCode.TooManyRequests, Constants.ApiErrorCodes.RateLimited,
            _busyMessage, retryAfterSeconds: _retryAfterSeconds);
    }
}
