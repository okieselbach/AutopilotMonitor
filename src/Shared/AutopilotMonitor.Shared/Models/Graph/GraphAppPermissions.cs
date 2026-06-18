namespace AutopilotMonitor.Shared.Models.Graph
{
    /// <summary>
    /// Constant catalog of Microsoft Graph application permission names referenced by the
    /// Autopilot Monitor backend. Centralised so feature toggles, the customer-side grant
    /// script, and the runtime permission detector cannot drift apart.
    /// </summary>
    public static class GraphAppPermissions
    {
        /// <summary>
        /// Core enrollment device-validation permission. Required to read
        /// <c>windowsAutopilotDeviceIdentities</c> (Autopilot Device Validation) and
        /// <c>importedDeviceIdentities</c> (Corporate Identifier Validation). Part of the
        /// publisher manifest's default-consent set — present in the SP's token <c>roles</c>
        /// claim once admin consent has been granted by anyone in the tenant. Used by the
        /// access-check probe to silently reconcile pre-approved consent.
        /// </summary>
        public const string DeviceManagementServiceConfigReadAll = "DeviceManagementServiceConfig.Read.All";

        /// <summary>Read display names + file names of Intune device management scripts (Platform Scripts + Remediations).</summary>
        public const string DeviceManagementScriptsReadAll = "DeviceManagementScripts.Read.All";

        /// <summary>Read Intune configuration profiles (future use — policy display names).</summary>
        public const string DeviceManagementConfigurationReadAll = "DeviceManagementConfiguration.Read.All";

        /// <summary>Read Intune managed apps (future use — Win32 app display names not yet covered).</summary>
        public const string DeviceManagementAppsReadAll = "DeviceManagementApps.Read.All";
    }
}
