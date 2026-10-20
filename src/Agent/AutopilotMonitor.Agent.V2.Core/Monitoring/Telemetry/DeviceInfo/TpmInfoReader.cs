using System.Management;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo
{
    /// <summary>The TPM as <c>Win32_Tpm</c> reports it.</summary>
    internal sealed class TpmInfo
    {
        public bool IsActivated { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsOwned { get; set; }

        /// <summary><c>ManufacturerIdTxt</c> as reported — may be space-padded (<c>"STM "</c>).</summary>
        public string ManufacturerName { get; set; }

        /// <summary><c>ManufacturerVersion</c>: the firmware version.</summary>
        public string ManufacturerVersion { get; set; }

        /// <summary>First element of <c>SpecVersion</c> (<c>"2.0, 0, 1.59"</c> → <c>"2.0"</c>); null when absent.</summary>
        public string SpecVersion { get; set; }

        /// <summary>Manufacturer id and firmware version, trimmed: <c>IFX 7.63.3353.0</c>; null when both are empty.</summary>
        public string Label => FormatLabel(ManufacturerName, ManufacturerVersion);

        internal static string FormatLabel(string manufacturerName, string manufacturerVersion)
        {
            var label = $"{manufacturerName?.Trim()} {manufacturerVersion?.Trim()}".Trim();
            return label.Length == 0 ? null : label;
        }
    }

    /// <summary>
    /// Reads <c>Win32_Tpm</c> from <c>root\CIMV2\Security\MicrosoftTpm</c>: one WMI connect plus one
    /// query. Shared by the <c>tpm_status</c> device-info event and the TPM-PSS distress report.
    /// </summary>
    internal static class TpmInfoReader
    {
        /// <summary>The last listed TPM instance (there is one in practice); null when WMI lists none. Throws on WMI failure.</summary>
        public static TpmInfo Read()
        {
            TpmInfo info = null;
            using (var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm"),
                new ObjectQuery("SELECT IsActivated_InitialValue, IsEnabled_InitialValue, IsOwned_InitialValue, ManufacturerIdTxt, ManufacturerVersion, SpecVersion FROM Win32_Tpm")))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    var specVersion = obj["SpecVersion"]?.ToString();
                    info = new TpmInfo
                    {
                        IsActivated = obj["IsActivated_InitialValue"]?.ToString() == "True",
                        IsEnabled = obj["IsEnabled_InitialValue"]?.ToString() == "True",
                        IsOwned = obj["IsOwned_InitialValue"]?.ToString() == "True",
                        ManufacturerName = obj["ManufacturerIdTxt"]?.ToString(),
                        ManufacturerVersion = obj["ManufacturerVersion"]?.ToString(),
                        SpecVersion = string.IsNullOrEmpty(specVersion) ? null : specVersion.Split(',')[0].Trim(),
                    };
                }
            }
            return info;
        }
    }
}
