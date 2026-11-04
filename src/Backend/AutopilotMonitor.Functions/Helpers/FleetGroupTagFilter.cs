using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// The Fleet Health view filter by Autopilot group tag: sessions whose group tag is listed, or that
    /// carry none with <see cref="ExcludeUntagged"/>, leave every figure of the view. Nothing is stored —
    /// the filter lives in the request, and alerts never see it. Tags compare trimmed and without case.
    /// </summary>
    public sealed class FleetGroupTagFilter
    {
        /// <summary>Most group tags one request may exclude.</summary>
        internal const int MaxExcludedTags = 20;

        public static readonly FleetGroupTagFilter None = new(Array.Empty<string>(), excludeUntagged: false);

        private readonly HashSet<string> _excluded;

        private FleetGroupTagFilter(IReadOnlyList<string> excludedTags, bool excludeUntagged)
        {
            ExcludedTags = excludedTags;
            ExcludeUntagged = excludeUntagged;
            _excluded = new HashSet<string>(excludedTags, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The excluded group tags as requested: trimmed, de-duplicated without case.</summary>
        public IReadOnlyList<string> ExcludedTags { get; }

        /// <summary>Whether the sessions without a group tag leave the view.</summary>
        public bool ExcludeUntagged { get; }

        public bool IsActive => ExcludedTags.Count > 0 || ExcludeUntagged;

        /// <summary>The bucket key of a session: its trimmed group tag, empty when it has none.</summary>
        internal static string KeyOf(SessionSummary session) => session.AutopilotGroupTag?.Trim() ?? string.Empty;

        public bool Excludes(SessionSummary session) => ExcludesKey(KeyOf(session));

        internal bool ExcludesKey(string key) => key.Length == 0 ? ExcludeUntagged : _excluded.Contains(key);

        /// <summary>
        /// Parses <c>excludeGroupTags</c> (comma-separated, at most <see cref="MaxExcludedTags"/>) and
        /// <c>excludeUntagged</c> ("true"). False with an error message for too many or overlong tags.
        /// </summary>
        public static bool TryParse(string? excludeGroupTags, string? excludeUntagged, out FleetGroupTagFilter filter, out string? error)
        {
            filter = None;
            error = null;
            var tags = (excludeGroupTags ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tags.Count > MaxExcludedTags)
            {
                error = $"excludeGroupTags takes at most {MaxExcludedTags} comma-separated group tags";
                return false;
            }
            if (tags.Any(t => t.Length > AutopilotDeviceValidator.MaxRegistrationFieldLength))
            {
                error = $"a group tag in excludeGroupTags is longer than {AutopilotDeviceValidator.MaxRegistrationFieldLength} characters";
                return false;
            }

            var untagged = string.Equals(excludeUntagged?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            if (tags.Count > 0 || untagged)
                filter = new FleetGroupTagFilter(tags, untagged);
            return true;
        }
    }
}
