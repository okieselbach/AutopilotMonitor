using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    /// <summary>
    /// POST /api/global/raw/logs — the operator KQL proxy. Forwards an operator-supplied query VERBATIM
    /// to one of the telemetry stores in <see cref="LogQuerySourceCatalog"/> (backend App Insights,
    /// web App Insights, the MCP Container App's Log Analytics workspace) under the Function App's
    /// managed identity and returns the Kusto result tables typed (<see cref="QueryBackendLogsResponse"/>).
    /// <para>
    /// Parity with <c>az monitor app-insights query</c> / <c>az monitor log-analytics query</c> is by
    /// construction: same REST endpoint, same <c>{query, timespan}</c> body, tables forwarded cell for
    /// cell. What the proxy adds is what the CLI leaves implicit — the source, the wall-clock budget the
    /// call ran under (<c>Prefer: wait</c> upstream, a cancellation token here, no static client
    /// timeout), an explicit <c>partial</c> flag when the store truncated the result, and the full
    /// upstream error JSON on failure. There is no continuation: Kusto has none; a query that does not
    /// fit the budget is narrowed (smaller timespan, <c>summarize</c>), not resumed.
    /// </para>
    /// Authorization: catalog <c>GlobalAdminOnly</c> + policy middleware, re-checked in the body.
    /// </summary>
    public class AppInsightsQueryFunction
    {
        internal const int DefaultBudgetSeconds = 30;
        internal const int MinBudgetSeconds = 5;
        /// <summary>Stays well under the ~230 s Azure HTTP front-end limit plus the MCP's own margin.</summary>
        internal const int MaxBudgetSeconds = 180;
        /// <summary>Upstream error bodies are forwarded whole up to this size so nothing az would show is lost.</summary>
        private const int UpstreamErrorCap = 8192;

        private readonly ILogger<AppInsightsQueryFunction> _logger;
        private readonly LogQuerySourceCatalog _sources;
        private readonly LogQueryExecutor _executor;

        public AppInsightsQueryFunction(
            ILogger<AppInsightsQueryFunction> logger,
            LogQuerySourceCatalog sources,
            LogQueryExecutor executor)
        {
            _logger = logger;
            _sources = sources;
            _executor = executor;
        }

        /// <summary>
        /// Body: <c>{ "query": "traces | ...", "timespan": "PT1H", "source": "backend", "budgetSeconds": 30 }</c>.
        /// </summary>
        [Function("QueryBackendLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/raw/logs")] HttpRequestData req,
            FunctionContext context)
        {
            // Authorization is the catalog (GlobalAdminOnly) + PolicyEnforcementMiddleware; this is the
            // in-body re-check so a middleware regression can never expose the KQL proxy.
            if (await RawGlobalAdminGate.DenyUnlessGlobalAdminAsync(req, context) is { } denied)
                return denied;

            var read = await req.ReadAsync<LogQueryRequest>();
            if (read.Error != null) return read.Error;
            var body = read.Value!;
            if (string.IsNullOrWhiteSpace(body.Query))
            {
                return await req.BadRequestAsync("query is required");
            }

            if (!_sources.TryResolve(body.Source, out var source, out var sourceError))
            {
                var known = LogQuerySources.IsKnown(string.IsNullOrWhiteSpace(body.Source) ? LogQuerySources.Backend : body.Source.Trim());
                var status = known ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest;
                return await req.ErrorAsync(status, ApiErrorWriter.DefaultCode(status), sourceError ?? "Unknown log source.");
            }

            var timespan = string.IsNullOrWhiteSpace(body.Timespan) ? "PT1H" : body.Timespan.Trim();
            var budget = ClampBudget(body.BudgetSeconds);

            try
            {
                var outcome = await _executor.ExecuteAsync(source!, body.Query, timespan, budget);

                if (!outcome.Succeeded)
                {
                    var upstreamStatus = outcome.UpstreamStatus ?? HttpStatusCode.BadGateway;
                    var responseText = outcome.UpstreamBody ?? string.Empty;
                    _logger.LogWarning("Log query ({Source}) failed: {StatusCode} {Response}",
                        source!.Name, upstreamStatus, LogSanitizer.Clean(Cap(responseText, 2048)));

                    var (errorMessage, errorCode) = LogQueryExecutor.ExtractError(responseText);
                    // A query the store rejected is the CALLER's error (400): the MCP renders 4xx bodies in
                    // full (message, hint, upstream) but sanitizes every 5xx down to the error code — and a
                    // KQL syntax error hidden behind "retry later" is exactly the parity loss this proxy
                    // exists to avoid. Store-side failures (5xx) and a missing read grant (401/403 — the
                    // managed identity, not the caller) stay 502.
                    var status = MapUpstreamFailure(upstreamStatus);
                    return await req.ErrorAsync(status, new QueryBackendLogsErrorResponse
                    {
                        Error = errorMessage ?? "The telemetry store rejected the query.",
                        Code = ApiErrorWriter.DefaultCode(status),
                        UpstreamCode = errorCode,
                        StatusCode = (int)upstreamStatus,
                        Source = source.Name,
                        Hint = UpstreamHint(upstreamStatus, errorCode, source.Name),
                        // Everything the store said, so nothing the CLI would print is lost.
                        Upstream = Cap(responseText, UpstreamErrorCap),
                    });
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new QueryBackendLogsResponse
                {
                    Success = true,
                    Source = source!.Name,
                    Timespan = timespan,
                    BudgetSeconds = budget,
                    ElapsedMs = outcome.ElapsedMs,
                    Partial = outcome.PartialReason != null ? true : null,
                    PartialReason = outcome.PartialReason,
                    Tables = outcome.Tables!,
                });
                return response;
            }
            catch (OperationCanceledException)
            {
                return await req.ErrorAsync(HttpStatusCode.GatewayTimeout, Constants.ApiErrorCodes.UpstreamTimeout,
                    $"Log query ({source!.Name}) exceeded its budget of {budget}s",
                    hint: $"Narrow the query (smaller timespan, `summarize` instead of raw rows, `take N`) or raise budgetSeconds (max {MaxBudgetSeconds}).");
            }
            catch (Azure.Identity.CredentialUnavailableException ex)
            {
                _logger.LogError(ex, "Managed Identity not available for log query ({Source})", source!.Name);
                return await req.ErrorAsync(HttpStatusCode.ServiceUnavailable, Constants.ApiErrorCodes.ServiceUnavailable, "Managed Identity is not configured. Enable the system-assigned Managed Identity on the Function App and grant it read access to the telemetry store.");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query telemetry store");
            }
        }

        /// <summary>
        /// Upstream 4xx other than 401/403 = the caller's query (400 to the caller, rendered in full by the
        /// MCP); 401/403 = the managed identity lacks the read grant, and 5xx = the store — both 502.
        /// </summary>
        internal static HttpStatusCode MapUpstreamFailure(HttpStatusCode upstream)
        {
            var code = (int)upstream;
            if (code == 401 || code == 403) return HttpStatusCode.BadGateway;
            return code >= 400 && code < 500 ? HttpStatusCode.BadRequest : HttpStatusCode.BadGateway;
        }

        internal static string? UpstreamHint(HttpStatusCode upstream, string? errorCode, string sourceName)
        {
            if ((int)upstream is 401 or 403)
                return $"The backend's managed identity has no read access to the '{sourceName}' store (Monitoring Reader on the App Insights component / Log Analytics Reader on the workspace). Role propagation can take minutes after a grant.";
            return errorCode switch
            {
                "SyntaxError" => "The KQL query has a syntax error. Check operators, pipe stages, and string quoting. Example: traces | where message contains 'error' | take 50",
                "SemanticError" => $"The KQL query references an unknown table or column for source '{sourceName}'. Run '<table> | getschema' to list columns.",
                _ => null,
            };
        }

        /// <summary>Null or out-of-range budgets collapse to the default / the nearest bound.</summary>
        internal static int ClampBudget(int? requested)
        {
            if (requested is null || requested <= 0)
                return DefaultBudgetSeconds;
            return Math.Clamp(requested.Value, MinBudgetSeconds, MaxBudgetSeconds);
        }

        private static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
