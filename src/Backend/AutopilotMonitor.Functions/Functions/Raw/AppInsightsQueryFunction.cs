using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Core;
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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TokenCredential _credential;

        public AppInsightsQueryFunction(
            ILogger<AppInsightsQueryFunction> logger,
            LogQuerySourceCatalog sources,
            IHttpClientFactory httpClientFactory,
            TokenCredential credential)
        {
            _logger = logger;
            _sources = sources;
            _httpClientFactory = httpClientFactory;
            _credential = credential;
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

            LogQueryRequest? body;
            try
            {
                body = await req.ReadFromJsonAsync<LogQueryRequest>();
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body == null || string.IsNullOrWhiteSpace(body.Query))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { error = "query is required" });
                return bad;
            }

            if (!_sources.TryResolve(body.Source, out var source, out var sourceError))
            {
                var known = LogQuerySources.IsKnown(string.IsNullOrWhiteSpace(body.Source) ? LogQuerySources.Backend : body.Source.Trim());
                var status = known ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest;
                var resp = req.CreateResponse(status);
                await resp.WriteAsJsonAsync(new { error = sourceError });
                return resp;
            }

            var timespan = string.IsNullOrWhiteSpace(body.Timespan) ? "PT1H" : body.Timespan.Trim();
            var budget = ClampBudget(body.BudgetSeconds);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(budget));

                var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { source!.TokenScope }), cts.Token);

                using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, source.QueryUri);
                upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                // Ask the store to keep working as long as we are willing to wait (default server wait is shorter).
                upstreamRequest.Headers.TryAddWithoutValidation("Prefer", $"wait={budget}");
                upstreamRequest.Content = new StringContent(
                    JsonSerializer.Serialize(new KustoQuery(body.Query, timespan)), Encoding.UTF8, "application/json");

                var client = _httpClientFactory.CreateClient(LogQuerySourceCatalog.HttpClientName);
                using var upstreamResponse = await client.SendAsync(upstreamRequest, cts.Token);
                var responseText = await upstreamResponse.Content.ReadAsStringAsync(cts.Token);
                stopwatch.Stop();

                if (!upstreamResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Log query ({Source}) failed: {StatusCode} {Response}",
                        source.Name, upstreamResponse.StatusCode, LogSanitizer.Clean(Cap(responseText, 2048)));

                    var (errorMessage, errorCode) = ExtractError(responseText);
                    var errorResp = req.CreateResponse(HttpStatusCode.BadGateway);
                    await errorResp.WriteAsJsonAsync(new
                    {
                        error = errorMessage,
                        errorCode,
                        statusCode = (int)upstreamResponse.StatusCode,
                        source = source.Name,
                        hint = errorCode == "SyntaxError"
                            ? "The KQL query has a syntax error. Check operators, pipe stages, and string quoting. Example: traces | where message contains 'error' | take 50"
                            : errorCode == "SemanticError"
                                ? $"The KQL query references an unknown table or column for source '{source.Name}'. Run '<table> | getschema' to list columns."
                                : (string?)null,
                        // Everything the store said, so nothing the CLI would print is lost.
                        upstream = Cap(responseText, UpstreamErrorCap),
                    });
                    return errorResp;
                }

                var (tables, partialReason) = ParseKustoBody(responseText);
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new QueryBackendLogsResponse
                {
                    Success = true,
                    Source = source.Name,
                    Timespan = timespan,
                    BudgetSeconds = budget,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    Partial = partialReason != null ? true : null,
                    PartialReason = partialReason,
                    Tables = tables,
                });
                return response;
            }
            catch (OperationCanceledException)
            {
                var timeout = req.CreateResponse(HttpStatusCode.GatewayTimeout);
                await timeout.WriteAsJsonAsync(new
                {
                    error = $"Log query ({source!.Name}) exceeded its budget of {budget}s",
                    budgetSeconds = budget,
                    hint = $"Narrow the query (smaller timespan, `summarize` instead of raw rows, `take N`) or raise budgetSeconds (max {MaxBudgetSeconds}).",
                });
                return timeout;
            }
            catch (Azure.Identity.CredentialUnavailableException ex)
            {
                _logger.LogError(ex, "Managed Identity not available for log query ({Source})", source!.Name);
                var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                await err.WriteAsJsonAsync(new { error = "Managed Identity is not configured. Enable the system-assigned Managed Identity on the Function App and grant it read access to the telemetry store." });
                return err;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query telemetry store");
            }
        }

        /// <summary>Null or out-of-range budgets collapse to the default / the nearest bound.</summary>
        internal static int ClampBudget(int? requested)
        {
            if (requested is null || requested <= 0)
                return DefaultBudgetSeconds;
            return Math.Clamp(requested.Value, MinBudgetSeconds, MaxBudgetSeconds);
        }

        /// <summary>
        /// Kusto REST success body → typed tables plus the store's partial-result note. A 200 can carry
        /// <c>"error": { "code": "PartialError", ... }</c> next to <c>tables</c> when the result was cut
        /// (size cap, shard timeout); that used to pass through unread. Cells are cloned out of the
        /// document so they outlive it.
        /// </summary>
        internal static (IReadOnlyList<KqlTable> Tables, string? PartialReason) ParseKustoBody(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tables = new List<KqlTable>();

            if (root.TryGetProperty("tables", out var tablesEl) && tablesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tablesEl.EnumerateArray())
                {
                    var columns = new List<KqlColumn>();
                    if (t.TryGetProperty("columns", out var colsEl) && colsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in colsEl.EnumerateArray())
                        {
                            columns.Add(new KqlColumn
                            {
                                Name = c.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                                Type = c.TryGetProperty("type", out var ty) ? ty.GetString() ?? string.Empty : string.Empty,
                            });
                        }
                    }

                    var rows = new List<IReadOnlyList<JsonElement>>();
                    if (t.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rowsEl.EnumerateArray())
                        {
                            var cells = new List<JsonElement>(columns.Count);
                            if (r.ValueKind == JsonValueKind.Array)
                                foreach (var cell in r.EnumerateArray())
                                    cells.Add(cell.Clone());
                            rows.Add(cells);
                        }
                    }

                    tables.Add(new KqlTable
                    {
                        Name = t.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                        Columns = columns,
                        Rows = rows,
                    });
                }
            }

            string? partialReason = null;
            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object)
            {
                var (message, code) = ExtractError(errorEl);
                partialReason = string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
            }

            return (tables, partialReason);
        }

        /// <summary>
        /// Message + code from a Kusto error body — <c>innererror</c> first (the specific reason), then the
        /// outer <c>error</c>. Tolerates non-JSON.
        /// </summary>
        internal static (string Message, string? Code) ExtractError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var errorObj) && errorObj.ValueKind == JsonValueKind.Object)
                    return ExtractError(errorObj);
            }
            catch (JsonException)
            {
                // not JSON — keep the generic message
            }
            return ("Telemetry store query failed", null);
        }

        private static (string Message, string? Code) ExtractError(JsonElement errorObj)
        {
            var message = "Telemetry store query failed";
            string? code = null;

            if (errorObj.TryGetProperty("innererror", out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                if (inner.TryGetProperty("message", out var innerMsg) && innerMsg.ValueKind == JsonValueKind.String)
                    message = innerMsg.GetString() ?? message;
                if (inner.TryGetProperty("code", out var innerCode) && innerCode.ValueKind == JsonValueKind.String)
                    code = innerCode.GetString();
            }
            if (message == "Telemetry store query failed" && errorObj.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                message = msg.GetString() ?? message;
            if (code == null && errorObj.TryGetProperty("code", out var outerCode) && outerCode.ValueKind == JsonValueKind.String)
                code = outerCode.GetString();

            return (message, code);
        }

        private static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        private sealed class LogQueryRequest
        {
            public string Query { get; set; } = string.Empty;
            public string? Timespan { get; set; }
            /// <summary>One of <see cref="LogQuerySources"/>; defaults to backend.</summary>
            public string? Source { get; set; }
            /// <summary>Wall-clock budget for the upstream call; clamped to 5..180, default 30.</summary>
            public int? BudgetSeconds { get; set; }
        }

        /// <summary>Kusto REST request body — both App Insights and Log Analytics accept exactly this.</summary>
        private sealed record KustoQuery(
            [property: JsonPropertyName("query")] string Query,
            [property: JsonPropertyName("timespan")] string Timespan);
    }
}
