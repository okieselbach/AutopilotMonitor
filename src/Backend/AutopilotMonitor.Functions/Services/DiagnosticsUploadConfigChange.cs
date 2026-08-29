using System;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// "Can an on-demand diagnostics upload succeed for this tenant?" — the single definition
    /// behind the session-detail Collect Logs button (feature flag
    /// <c>diagnosticsUploadConfigured</c>) and the ops-event tripwire that reports when a
    /// tenant admin turns that capability on or off (portal Settings, the Collect Logs
    /// quick-config dialog, MCP <c>update_tenant_config</c>, or a config revert).
    /// </summary>
    public sealed class DiagnosticsUploadConfigChange
    {
        public bool Enabled { get; }
        public string? Destination { get; }
        public string Mode { get; }

        private DiagnosticsUploadConfigChange(bool enabled, string? destination, string mode)
        {
            Enabled = enabled;
            Destination = destination;
            Mode = mode;
        }

        /// <summary>
        /// Mode not Off AND a usable destination (customer SAS or Hosted). Mirrors the agent's
        /// own gate (<c>DiagnosticsUploadMode</c> + <c>DiagnosticsUploadEnabled</c>), so
        /// "configured" here means the agent would actually build and upload a package.
        /// </summary>
        public static bool IsConfigured(TenantConfiguration? config)
        {
            if (config == null) return false;
            return !string.Equals(config.DiagnosticsUploadMode ?? "Off", "Off", StringComparison.OrdinalIgnoreCase)
                && GetAgentConfigFunction.ResolveDiagnosticsUploadEnabled(
                    config.DiagnosticsBlobSasUrl, config.DiagnosticsUploadDestination);
        }

        /// <summary>
        /// Returns the flip when <paramref name="after"/> changes the configured state relative
        /// to <paramref name="before"/>; null when the capability did not change (including
        /// destination/mode edits that keep it on). A missing <paramref name="before"/> row
        /// counts as "not configured".
        /// </summary>
        public static DiagnosticsUploadConfigChange? Detect(TenantConfiguration? before, TenantConfiguration after)
        {
            if (after == null) throw new ArgumentNullException(nameof(after));

            var wasConfigured = IsConfigured(before);
            var isConfigured = IsConfigured(after);
            if (wasConfigured == isConfigured) return null;

            return new DiagnosticsUploadConfigChange(
                isConfigured,
                after.DiagnosticsUploadDestination,
                after.DiagnosticsUploadMode ?? "Off");
        }
    }
}
