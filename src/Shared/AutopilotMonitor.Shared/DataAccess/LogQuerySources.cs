using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// The telemetry stores the operator KQL proxy (<c>POST /api/global/raw/logs</c>, MCP
    /// <c>query_backend_logs</c>) can be pointed at. Wire values are lowercase and travel as the
    /// request's <c>source</c> field; the MCP derives its <c>z.enum</c> from this list through the
    /// shared manifest (section <c>logSources</c>), so the vocabulary is never retyped.
    /// </summary>
    public static class LogQuerySources
    {
        /// <summary>The Functions backend's Application Insights component (requests, traces, exceptions, customEvents, dependencies).</summary>
        public const string Backend = "backend";
        /// <summary>The portal's Application Insights component (pageViews, browser customEvents, browserTimings, client dependencies).</summary>
        public const string Web = "web";
        /// <summary>The MCP Container App's Log Analytics workspace (ContainerAppConsoleLogs_CL / ContainerAppSystemLogs_CL).</summary>
        public const string Mcp = "mcp";

        /// <summary>Declaration order; the MCP renders the enum in this order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Backend, Web, Mcp };

        public static bool IsKnown(string? source)
            => source != null && Array.IndexOf(new[] { Backend, Web, Mcp }, source) >= 0;
    }
}
