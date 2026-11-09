using System;
using System.Linq;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Whether the setup guide's two optional steps are done. A step counts as done when an admin
    /// finished it in the guide, or when the configuration already says so — a tenant that set
    /// things up by hand never sees those steps as open. The two required steps (a validation
    /// method, the first session) come from the configuration and the enrollment counter.
    /// </summary>
    internal static class SetupGuideState
    {
        private static readonly TenantConfiguration Defaults = new();

        /// <summary>Recommended settings: timezone auto-set, the software inventory analyzer and a working diagnostics upload.</summary>
        internal static bool SettingsDone(TenantConfiguration config) =>
            config.SetupGuideSettingsReviewedAt != null
            || (config.EnableTimezoneAutoSet == true
                && config.EnableSoftwareInventoryAnalyzer == true
                && DiagnosticsUploadConfigChange.IsConfigured(config));

        /// <summary>Hardware: the whitelist was reviewed, or it differs from the default.</summary>
        internal static bool HardwareDone(TenantConfiguration config) =>
            config.SetupGuideHardwareReviewedAt != null
            || !SameEntries(config.GetManufacturerWhitelist(), Defaults.GetManufacturerWhitelist())
            || !SameEntries(config.GetModelWhitelist(), Defaults.GetModelWhitelist());

        private static bool SameEntries(string[] a, string[] b) =>
            a.Length == b.Length
            && a.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(b.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }
}
