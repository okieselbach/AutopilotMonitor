using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Pagination;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class TableQueryFunction
    {
        private readonly ILogger<TableQueryFunction> _logger;
        private readonly TableStorageService _storage;

        // Tables that must never be exposed (contain secrets)
        // NOTE: Intentionally empty during preview — only Global Admin (single user) has access
        // to this endpoint. Consider adding TenantConfiguration, AdminConfiguration, BootstrapSessions
        // before GA release when more users may have Global Admin access.
        private static readonly HashSet<string> _blacklistedTables = new(StringComparer.OrdinalIgnoreCase)
        {
        };

        public TableQueryFunction(ILogger<TableQueryFunction> logger, TableStorageService storage)
        {
            _logger = logger;
            _storage = storage;
        }

        /// <summary>
        /// GET /api/global/raw/tables — List all available table names
        /// </summary>
        [Function("ListRawTables")]
        public async Task<HttpResponseData> ListTables(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/tables")] HttpRequestData req)
        {
            try
            {
                var tables = Constants.TableNames.All
                    .Where(t => !_blacklistedTables.Contains(t))
                    .OrderBy(t => t)
                    .ToList();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { count = tables.Count, tables });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "List tables");
            }
        }

        /// <summary>
        /// GET /api/global/raw/tables/{tableName} — Query any table directly
        /// Query params: partitionKey, rowKeyPrefix, filter, limit
        /// </summary>
        [Function("QueryRawTable")]
        public async Task<HttpResponseData> QueryTable(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/tables/{tableName}")] HttpRequestData req,
            string tableName)
        {
            try
            {
                // Validate table name
                if (!Constants.TableNames.All.Contains(tableName) &&
                    !Constants.TableNames.All.Any(t => t.Equals(tableName, StringComparison.OrdinalIgnoreCase)))
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { error = $"Table '{tableName}' not found" });
                    return notFound;
                }

                if (_blacklistedTables.Contains(tableName))
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { error = $"Table '{tableName}' is not accessible" });
                    return forbidden;
                }

                // Resolve actual table name (case-insensitive match)
                var actualTableName = Constants.TableNames.All
                    .First(t => t.Equals(tableName, StringComparison.OrdinalIgnoreCase));

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var partitionKey = query["partitionKey"];
                var rowKeyPrefix = query["rowKeyPrefix"];
                var filter = query["filter"];

                var pagination = RawTablePagination.ParsePagination(query);
                if (pagination.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = pagination.Error });
                    return bad;
                }

                // Build OData filter
                var oDataFilter = BuildFilter(partitionKey, rowKeyPrefix, filter);

                var callerTenantId = TenantHelper.GetTenantId(req);

                string? azureToken = null;
                if (pagination.Continuation != null)
                {
                    if (!RawTablePagination.TryAcceptContinuation(
                            pagination.Continuation, callerTenantId, actualTableName,
                            partitionKey, rowKeyPrefix, filter,
                            out azureToken, out var rejectReason))
                    {
                        _logger.LogWarning("QueryRawTable: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            error = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var tableClient = _storage.GetTableClient(actualTableName);
                var (rawEntities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                    client: tableClient,
                    filter: oDataFilter,
                    pageSize: pagination.PageSize,
                    continuation: azureToken);

                var entities = new List<Dictionary<string, object?>>(rawEntities.Count);
                foreach (var entity in rawEntities)
                {
                    var dict = new Dictionary<string, object?>
                    {
                        ["PartitionKey"] = entity.PartitionKey,
                        ["RowKey"] = entity.RowKey,
                        ["Timestamp"] = entity.Timestamp,
                    };

                    foreach (var kvp in entity)
                    {
                        if (kvp.Key is "odata.etag" or "PartitionKey" or "RowKey" or "Timestamp")
                            continue;
                        dict[kvp.Key] = kvp.Value;
                    }

                    entities.Add(dict);
                }

                string? nextLink = null;
                if (!string.IsNullOrEmpty(nextRawToken))
                {
                    var fp = RawTablePagination.Fingerprint(
                        callerTenantId, actualTableName, partitionKey, rowKeyPrefix, filter);
                    var wireToken = ContinuationToken.Encode(nextRawToken!, callerTenantId, fp);
                    nextLink = RawTablePagination.BuildNextLink(actualTableName, pagination.PageSize, wireToken, query);
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    table = actualTableName,
                    count = entities.Count,
                    entities,
                    nextLink,
                });
                return response;
            }
            catch (RequestFailedException rfe) when (rfe.Status == 400)
            {
                // A malformed $filter / bad query input is a caller error, not a
                // server crash — Azure Table Storage returns 400. Surface it as 400
                // with the actionable Azure message so the caller fixes the filter,
                // instead of the generic 500 ("retry / contact an operator").
                _logger.LogInformation(rfe, "QueryRawTable: invalid query for table '{TableName}'", tableName);
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new
                {
                    error = "Invalid query parameters (filter / partitionKey / rowKeyPrefix). " +
                            "Check the OData filter syntax and that the column names exist.",
                    detail = rfe.Message,
                });
                return bad;
            }
            catch (RequestFailedException rfe)
            {
                return await req.InternalServerErrorAsync(_logger, rfe,
                    $"Query table '{tableName}'",
                    new { tableName, filter = req.Query["filter"], partitionKey = req.Query["partitionKey"] });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex,
                    $"Query table '{tableName}'",
                    new { tableName });
            }
        }

        private static string? BuildFilter(string? partitionKey, string? rowKeyPrefix, string? customFilter)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(partitionKey))
                parts.Add($"PartitionKey eq '{SanitizeOData(partitionKey)}'");

            if (!string.IsNullOrEmpty(rowKeyPrefix))
            {
                var sanitized = SanitizeOData(rowKeyPrefix);
                parts.Add($"RowKey ge '{sanitized}' and RowKey lt '{sanitized}~'");
            }

            if (!string.IsNullOrEmpty(customFilter))
            {
                // WARNING: This custom filter is passed through with minimal sanitization.
                // This is acceptable because this endpoint requires admin JWT auth,
                // but a proper OData filter allowlist should be considered in a future iteration.
                var sanitizedFilter = customFilter
                    .Replace(";", "")
                    .Replace("--", "");
                parts.Add($"({sanitizedFilter})");
            }

            return parts.Count > 0 ? string.Join(" and ", parts) : null;
        }

        private static string SanitizeOData(string value) => ODataSanitizer.EscapeValue(value);
    }
}
