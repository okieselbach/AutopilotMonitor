using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Configuration;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>One resolvable telemetry store behind the operator KQL proxy.</summary>
    /// <param name="Name">Wire name (<see cref="LogQuerySources"/>).</param>
    /// <param name="QueryUri">The store's Kusto query endpoint.</param>
    /// <param name="TokenScope">Entra scope the managed identity requests a token for.</param>
    /// <param name="SettingName">App setting that carries the store's id — named in the 503 when absent.</param>
    public sealed record LogQuerySource(string Name, Uri QueryUri, string TokenScope, string SettingName);

    /// <summary>
    /// Resolves a <c>source</c> name to the telemetry store the proxy queries. All three stores speak
    /// the same Kusto REST dialect (<c>{query, timespan}</c> in, <c>tables[].columns/rows</c> out) and
    /// differ only in host, resource id and token scope:
    /// <list type="bullet">
    /// <item><c>backend</c>, <c>web</c> — Application Insights components (App-ID from app settings), scope <c>api.applicationinsights.io</c>.</item>
    /// <item><c>mcp</c> — the Container App's Log Analytics workspace (customer id), scope <c>api.loganalytics.io</c>.</item>
    /// </list>
    /// The Function App's managed identity needs <c>Monitoring Reader</c> on each App Insights component
    /// and <c>Log Analytics Reader</c> on the workspace (infra: New-TargetInfrastructure.ps1). Settings are
    /// read once at construction; a missing one makes that source unavailable (503), never the others.
    /// </summary>
    public sealed class LogQuerySourceCatalog
    {
        public const string BackendAppIdSetting = "APPINSIGHTS_APP_ID";
        public const string WebAppIdSetting = "APPINSIGHTS_WEB_APP_ID";
        public const string McpWorkspaceIdSetting = "MCP_LOG_ANALYTICS_WORKSPACE_ID";

        /// <summary>Named HttpClient without a client-side timeout: the per-request budget governs.</summary>
        public const string HttpClientName = "log-query";

        private const string AppInsightsScope = "https://api.applicationinsights.io/.default";
        private const string LogAnalyticsScope = "https://api.loganalytics.io/.default";

        private readonly Dictionary<string, LogQuerySource> _sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _missing = new(StringComparer.Ordinal);

        public LogQuerySourceCatalog(IConfiguration configuration)
        {
            Register(LogQuerySources.Backend, configuration[BackendAppIdSetting], BackendAppIdSetting,
                id => new Uri($"https://api.applicationinsights.io/v1/apps/{id}/query"), AppInsightsScope);
            Register(LogQuerySources.Web, configuration[WebAppIdSetting], WebAppIdSetting,
                id => new Uri($"https://api.applicationinsights.io/v1/apps/{id}/query"), AppInsightsScope);
            Register(LogQuerySources.Mcp, configuration[McpWorkspaceIdSetting], McpWorkspaceIdSetting,
                id => new Uri($"https://api.loganalytics.io/v1/workspaces/{id}/query"), LogAnalyticsScope);
        }

        private void Register(string name, string? id, string settingName, Func<string, Uri> uri, string scope)
        {
            if (string.IsNullOrWhiteSpace(id))
                _missing[name] = settingName;
            else
                _sources[name] = new LogQuerySource(name, uri(id.Trim()), scope, settingName);
        }

        /// <summary>Sources that are configured on this instance.</summary>
        public IReadOnlyCollection<string> Available => _sources.Keys;

        /// <summary>
        /// Resolves <paramref name="name"/>. Returns false with <paramref name="error"/> naming the
        /// missing app setting (known but unconfigured) or the allowed vocabulary (unknown name).
        /// </summary>
        public bool TryResolve(string? name, out LogQuerySource? source, out string error)
        {
            source = null;
            error = string.Empty;
            var key = string.IsNullOrWhiteSpace(name) ? LogQuerySources.Backend : name.Trim();

            if (_sources.TryGetValue(key, out var found))
            {
                source = found;
                return true;
            }
            if (_missing.TryGetValue(key, out var setting))
            {
                error = $"Log source '{key}' is not configured on this backend. Set the {setting} app setting and grant the Function App's managed identity read access to that resource.";
                return false;
            }
            error = $"Unknown log source '{key}'. Valid sources: {string.Join(", ", LogQuerySources.All)}.";
            return false;
        }
    }
}
