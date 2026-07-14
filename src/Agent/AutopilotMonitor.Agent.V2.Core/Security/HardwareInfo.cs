using System;
using System.Management;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Helper for retrieving hardware information
    /// </summary>
    public static class HardwareInfo
    {
        /// <summary>Sentinel returned per field when WMI cannot supply a value.</summary>
        private const string Unknown = "Unknown";

        /// <summary>
        /// Gets the device manufacturer, model and serial number from WMI.
        /// <para>
        /// The Winmgmt (WMI) service is occasionally unavailable for a brief window during OOBE —
        /// exactly when the agent first starts and reads hardware for the security headers AND the
        /// registration row. A single failed read would poison both with "Unknown" (observed:
        /// session rows with Manufacturer/Model/SerialNumber = "Unknown" while the registry-based
        /// OS fields were correct). This one read is the single source of truth (headers, telemetry
        /// and — via the auth bundle — the registration body all reuse it), so it retries once after
        /// a short delay when any field is still unresolved. Per-field merge keeps a value that
        /// resolved on an earlier attempt even if a later query transiently fails.
        /// </para>
        /// </summary>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <param name="maxAttempts">Total read attempts (default 2 → one retry). Overridable for tests.</param>
        /// <param name="retryDelayMs">Delay between attempts in milliseconds. Overridable for tests.</param>
        /// <returns>Tuple of (Manufacturer, Model, SerialNumber)</returns>
        public static (string Manufacturer, string Model, string SerialNumber) GetHardwareInfo(
            AgentLogger logger = null,
            int maxAttempts = 2,
            int retryDelayMs = 1500)
        {
            string manufacturer = Unknown;
            string model = Unknown;
            string serialNumber = Unknown;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var read = TryReadHardware(logger, attempt);

                // Per-field merge: never overwrite an already-resolved value with a later "Unknown".
                if (IsResolved(read.Manufacturer)) manufacturer = read.Manufacturer;
                if (IsResolved(read.Model)) model = read.Model;
                if (IsResolved(read.SerialNumber)) serialNumber = read.SerialNumber;

                if (IsResolved(manufacturer) && IsResolved(model) && IsResolved(serialNumber))
                {
                    if (attempt > 1)
                        logger?.Info($"Hardware fully resolved on attempt {attempt}/{maxAttempts}.");
                    return (manufacturer, model, serialNumber);
                }

                if (attempt < maxAttempts)
                {
                    logger?.Warning(
                        $"Hardware read incomplete (Manufacturer={manufacturer}, Model={model}, SerialNumber={serialNumber}) — " +
                        $"WMI may not be ready yet. Retrying in {retryDelayMs}ms (attempt {attempt}/{maxAttempts}).");
                    if (retryDelayMs > 0)
                        Thread.Sleep(retryDelayMs);
                }
            }

            logger?.Warning(
                $"Hardware read still incomplete after {maxAttempts} attempt(s): " +
                $"Manufacturer={manufacturer}, Model={model}, SerialNumber={serialNumber}.");
            return (manufacturer, model, serialNumber);
        }

        /// <summary>
        /// A single best-effort WMI read. Never throws. Each query is isolated so a failure reading
        /// the BIOS serial does not discard a manufacturer/model that was already read successfully.
        /// </summary>
        private static (string Manufacturer, string Model, string SerialNumber) TryReadHardware(AgentLogger logger, int attempt)
        {
            string manufacturer = Unknown;
            string model = Unknown;
            string serialNumber = Unknown;

            // Manufacturer + Model from Win32_ComputerSystem.
            // Lenovo reports the marketing model name in Win32_ComputerSystemProduct.Version
            // instead of Win32_ComputerSystem.Model (which contains a generic platform string).
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                using (var collection = searcher.Get())
                {
                    foreach (var obj in collection)
                    {
                        using (obj)
                        {
                            manufacturer = obj["Manufacturer"]?.ToString() ?? Unknown;
                            if (manufacturer.IndexOf("lenovo", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                using (var lenovoSearcher = new ManagementObjectSearcher("SELECT Version FROM Win32_ComputerSystemProduct"))
                                using (var lenovoCollection = lenovoSearcher.Get())
                                {
                                    foreach (var lenovoObj in lenovoCollection)
                                    {
                                        using (lenovoObj)
                                        {
                                            model = lenovoObj["Version"]?.ToString() ?? Unknown;
                                        }
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                model = obj["Model"]?.ToString() ?? Unknown;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"Hardware read: Win32_ComputerSystem query failed (attempt {attempt}): {ex.Message}");
            }

            // Serial number from Win32_BIOS — isolated so a failure here keeps manufacturer/model.
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                using (var collection = searcher.Get())
                {
                    foreach (var obj in collection)
                    {
                        using (obj)
                        {
                            serialNumber = obj["SerialNumber"]?.ToString() ?? Unknown;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"Hardware read: Win32_BIOS query failed (attempt {attempt}): {ex.Message}");
            }

            logger?.Debug($"Hardware detected (attempt {attempt}): Manufacturer={manufacturer}, Model={model}, SerialNumber={serialNumber}");
            return (manufacturer, model, serialNumber);
        }

        /// <summary>
        /// A field counts as resolved when it is non-empty and not the "Unknown" fallback sentinel.
        /// "Unknown" is only ever produced by this class's own fallbacks, so it is safe to treat as
        /// "not yet read" for retry purposes.
        /// </summary>
        private static bool IsResolved(string value)
            => !string.IsNullOrWhiteSpace(value)
               && !string.Equals(value, Unknown, StringComparison.OrdinalIgnoreCase);
    }
}
