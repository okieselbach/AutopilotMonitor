using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutopilotMonitor.Shared.Models;
using Azure.Core;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// What one Kusto query against a telemetry store returned: the tables (plus the store's partial-result
    /// note) on success, or the store's status code and body when it answered non-2xx.
    /// </summary>
    public sealed class LogQueryOutcome
    {
        public IReadOnlyList<KqlTable>? Tables { get; init; }
        public string? PartialReason { get; init; }
        public HttpStatusCode? UpstreamStatus { get; init; }
        public string? UpstreamBody { get; init; }
        public long ElapsedMs { get; init; }

        public bool Succeeded => Tables != null;
    }

    /// <summary>
    /// Runs one Kusto query against a store from <see cref="LogQuerySourceCatalog"/> under the Function App's
    /// managed identity and a wall-clock budget (<c>Prefer: wait</c> upstream, a cancellation token here, no
    /// static client timeout). Shared by the GA-only KQL proxy (<c>QueryBackendLogs</c>), which forwards an
    /// operator's query verbatim, and by the fixed aggregate queries of the MCP usage overview. Throws
    /// <see cref="OperationCanceledException"/> when the budget runs out and lets credential errors through;
    /// each caller decides how to answer.
    /// </summary>
    public class LogQueryExecutor
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TokenCredential _credential;

        public LogQueryExecutor(IHttpClientFactory httpClientFactory, TokenCredential credential)
        {
            _httpClientFactory = httpClientFactory;
            _credential = credential;
        }

        public virtual async Task<LogQueryOutcome> ExecuteAsync(LogQuerySource source, string query, string timespan, int budgetSeconds)
        {
            var stopwatch = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(budgetSeconds));

            var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { source.TokenScope }), cts.Token);

            using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, source.QueryUri);
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            // Ask the store to keep working as long as we are willing to wait (default server wait is shorter).
            upstreamRequest.Headers.TryAddWithoutValidation("Prefer", $"wait={budgetSeconds}");
            upstreamRequest.Content = new StringContent(
                JsonSerializer.Serialize(new KustoQuery(query, timespan)), Encoding.UTF8, "application/json");

            var client = _httpClientFactory.CreateClient(LogQuerySourceCatalog.HttpClientName);
            using var upstreamResponse = await client.SendAsync(upstreamRequest, cts.Token);
            var responseText = await upstreamResponse.Content.ReadAsStringAsync(cts.Token);
            stopwatch.Stop();

            if (!upstreamResponse.IsSuccessStatusCode)
            {
                return new LogQueryOutcome
                {
                    UpstreamStatus = upstreamResponse.StatusCode,
                    UpstreamBody = responseText,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                };
            }

            var (tables, partialReason) = ParseKustoBody(responseText);
            return new LogQueryOutcome { Tables = tables, PartialReason = partialReason, ElapsedMs = stopwatch.ElapsedMilliseconds };
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

        /// <summary>Kusto REST request body — both App Insights and Log Analytics accept exactly this.</summary>
        private sealed record KustoQuery(
            [property: JsonPropertyName("query")] string Query,
            [property: JsonPropertyName("timespan")] string Timespan);
    }
}
