using System;
using System.IO.Enumeration;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates hardware manufacturer and model against whitelist
    /// Supports wildcard patterns (e.g., "Dell*", "HP EliteBook*")
    /// </summary>
    public static class HardwareWhitelistValidator
    {
        /// <summary>
        /// Longest device value an entry other than "*" is compared with. Real SMBIOS manufacturer
        /// and model strings are a few dozen characters; a longer header is not one, and the
        /// comparison costs value × entry length on every agent request (D-411).
        /// </summary>
        internal const int MaxMatchedValueLength = 256;

        /// <summary>
        /// Validates hardware against whitelist
        /// </summary>
        /// <param name="manufacturer">Device manufacturer</param>
        /// <param name="model">Device model</param>
        /// <param name="manufacturerWhitelist">Allowed manufacturers (supports wildcards)</param>
        /// <param name="modelWhitelist">Allowed models (supports wildcards)</param>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <returns>Validation result</returns>
        public static HardwareValidationResult ValidateHardware(
            string? manufacturer,
            string? model,
            string[] manufacturerWhitelist,
            string[] modelWhitelist,
            ILogger? logger = null)
        {

            if (string.IsNullOrEmpty(manufacturer))
            {
                return new HardwareValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Manufacturer not provided"
                };
            }

            if (string.IsNullOrEmpty(model))
            {
                return new HardwareValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Model not provided"
                };
            }

            // Check manufacturer whitelist
            var manufacturerMatch = manufacturerWhitelist.Any(pattern => MatchesWildcard(manufacturer, pattern));
            if (!manufacturerMatch)
            {
                logger?.LogWarning($"Hardware rejected: Manufacturer '{Shorten(manufacturer)}' not in whitelist");
                return new HardwareValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Manufacturer '{Shorten(manufacturer)}' is not authorized",
                    Manufacturer = manufacturer,
                    Model = model
                };
            }

            // Check model whitelist
            var modelMatch = modelWhitelist.Any(pattern => MatchesWildcard(model, pattern));
            if (!modelMatch)
            {
                logger?.LogWarning($"Hardware rejected: Model '{Shorten(model)}' not in whitelist");
                return new HardwareValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Model '{Shorten(model)}' is not authorized",
                    Manufacturer = manufacturer,
                    Model = model
                };
            }

            logger?.LogDebug($"Hardware validated: Manufacturer={manufacturer}, Model={model}");
            return new HardwareValidationResult
            {
                IsValid = true,
                Manufacturer = manufacturer,
                Model = model
            };
        }

        /// <summary>
        /// Whole-value wildcard match, case-insensitive: '*' is any run of characters (also none),
        /// '?' exactly one, everything else literal. The tenant writes the entries and the device
        /// the value, so no Regex is built from them: the BCL's simple-expression matcher needs at
        /// most value × entry steps and never backtracks (D-411). Its escape character is '\',
        /// which is literal in an entry, so it is doubled first. Case folding is ordinal — SMBIOS
        /// strings are ASCII.
        /// </summary>
        internal static bool MatchesWildcard(string text, string pattern)
        {
            if (pattern == "*")
                return true;
            if (text.Length > MaxMatchedValueLength)
                return false;

            return FileSystemName.MatchesSimpleExpression(pattern.Replace("\\", "\\\\"), text, ignoreCase: true);
        }

        /// <summary>The value as a rejection message shows it: never longer than a real hardware string.</summary>
        private static string Shorten(string value)
            => value.Length <= MaxMatchedValueLength ? value : value.Substring(0, MaxMatchedValueLength) + "…";
    }

    /// <summary>
    /// Result of hardware validation
    /// </summary>
    public class HardwareValidationResult
    {
        /// <summary>
        /// Whether the hardware is authorized
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Device manufacturer
        /// </summary>
        public string? Manufacturer { get; set; }

        /// <summary>
        /// Device model
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// Error message if validation failed
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
