namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Length bound for every signal-payload-derived string that is stored into
    /// <see cref="DecisionState"/> (package ids, app ids, outcome/category/pattern
    /// identifiers, version strings).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unbounded payload string becomes an unbounded state string — copied into every
    /// snapshot persist, every audit trail and every dedupe comparison. Bounding at the
    /// reducer boundary keeps state size and per-signal work independent of what a forged
    /// or corrupted payload carries.
    /// </para>
    /// <para>
    /// The bound never truncates real telemetry: the strings it covers are registry key names
    /// (Windows caps those at 255 characters), IME app GUIDs, fixed enum-like outcome strings
    /// and SemVer version strings. Anything longer is by construction not something a device
    /// can observe.
    /// </para>
    /// </remarks>
    public static class FactStringBounds
    {
        /// <summary>One rule for all fact strings; <see cref="RealmJoinPackageFact.MaxDisplayNameLength"/> aliases it.</summary>
        public const int MaxLength = 256;

        /// <summary>Truncate <paramref name="value"/> to <see cref="MaxLength"/>; null and short values pass through unchanged.</summary>
        public static string? Bound(string? value)
        {
            if (value == null || value.Length <= MaxLength) return value;
            return value.Substring(0, MaxLength);
        }
    }
}
