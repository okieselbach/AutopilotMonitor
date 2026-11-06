namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Names of the request-row dimensions <c>RequestTelemetryMiddleware</c> writes on every API call that the
/// backend itself reads back with KQL (the MCP usage overview). Writer and reader use these constants, so a
/// rename cannot silently empty a query.
/// </summary>
internal static class RequestRowDimensions
{
    public const string Source = "Source";
    /// <summary>The value of <see cref="Source"/> on the worker's canonical request row.</summary>
    public const string WorkerMiddleware = "WorkerMiddleware";
    public const string FunctionName = "FunctionName";
    public const string HttpMethod = "HttpMethod";
    public const string HttpPath = "HttpPath";
    public const string ClientSource = "ClientSource";
    public const string McpToolName = "McpToolName";
    public const string CorrelationId = "CorrelationId";
    public const string TenantId = "TenantId";
    /// <summary>The caller's UPN, or <c>app:&lt;client-id&gt;</c> for an application principal.</summary>
    public const string UserId = "UserId";
    public const string UserRole = "UserRole";
    /// <summary>
    /// The tenant a Global Admin viewed as its member (read-only "view as"). Set on the request row and on every
    /// item the request emits; TenantId/UserRole of such a row stay the real caller's, never the viewed tenant's.
    /// </summary>
    public const string ViewAsTenantId = "ViewAsTenantId";
}
