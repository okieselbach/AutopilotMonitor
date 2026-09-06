using Microsoft.AspNetCore.Http;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The self-declared <c>X-Client-Source</c> request header. The MCP server stamps <c>mcp</c> on every
/// call it forwards; the value is caller-supplied and therefore only ever selects a MORE restrictive or
/// more descriptive path (quota layer, tenant MCP switch, richer error envelopes) — never a grant.
/// </summary>
internal static class ClientSourceHeader
{
    public const string HeaderName = "X-Client-Source";
    public const string Mcp = "mcp";

    /// <summary>True when the request declares itself as forwarded by the MCP server.</summary>
    public static bool IsMcp(HttpContext? httpContext)
        => httpContext != null && string.Equals(
            httpContext.Request.Headers[HeaderName].FirstOrDefault(), Mcp, StringComparison.OrdinalIgnoreCase);
}
