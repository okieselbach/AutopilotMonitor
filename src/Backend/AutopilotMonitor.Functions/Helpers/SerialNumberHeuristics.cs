namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Decides whether a reported serial number identifies a real physical device. OEM
    /// placeholder strings (VM defaults, unset BIOS fields) would cross-match unrelated
    /// devices and must never drive device-identity logic such as the registration
    /// supersede pass (misclassification audit 2026-07-16).
    /// </summary>
    public static class SerialNumberHeuristics
    {
        private static readonly HashSet<string> PlaceholderSerials = new(StringComparer.OrdinalIgnoreCase)
        {
            "0", "none", "unknown", "default string", "to be filled by o.e.m.",
            "system serial number", "not specified", "not applicable", "n/a", "na", "invalid",
        };

        public static bool IsUsableSerialNumber(string? serialNumber)
        {
            var trimmed = serialNumber?.Trim();
            return !string.IsNullOrEmpty(trimmed)
                && trimmed.Length >= 4
                && !PlaceholderSerials.Contains(trimmed);
        }
    }
}
