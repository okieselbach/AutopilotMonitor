using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Manages version-based block/kill rules for catching old or rogue agent versions.
    /// Patterns:
    ///   "1.*"      — matches all agents with major version 1
    ///   "1.0.*"    — matches all agents with major.minor 1.0
    ///   "1.0.30"   — matches all agents with version &lt;= 1.0.30
    ///   "=1.0.30"  — matches exactly version 1.0.30
    /// Global rules (apply to all tenants). Uses IDeviceSecurityRepository + in-memory cache.
    /// </summary>
    public class BlockedVersionService
    {
        private readonly IDeviceSecurityRepository _securityRepo;
        private readonly ILogger<BlockedVersionService> _logger;

        // 30s window mirrors BlockedDeviceService.EntryRevalidateAfter so a freshly-added
        // version Kill propagates across Function App instances within seconds, not minutes.
        // Each refresh is one table scan; with global rules the row count is small.
        private static readonly TimeSpan CacheRefreshInterval = TimeSpan.FromSeconds(30);

        // In-memory cache of all version block rules
        private readonly ConcurrentDictionary<string, VersionBlockCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private volatile bool _loaded;
        private DateTime _lastLoadedUtc = DateTime.MinValue;

        public BlockedVersionService(IDeviceSecurityRepository securityRepo, ILogger<BlockedVersionService> logger)
        {
            _securityRepo = securityRepo;
            _logger = logger;
        }

        /// <summary>
        /// Checks whether a given agent version matches any active block/kill rule.
        /// Returns the most severe action (Kill > Block).
        /// </summary>
        public async Task<(bool isBlocked, string action, string? matchedPattern)> IsVersionBlockedAsync(string agentVersion)
        {
            if (string.IsNullOrEmpty(agentVersion))
                return (false, "Block", null);

            if (!_loaded || DateTime.UtcNow - _lastLoadedUtc > CacheRefreshInterval)
                await LoadBlockListAsync();

            string? matchedAction = null;
            string? matchedPattern = null;

            foreach (var kvp in _cache)
            {
                if (VersionMatchesPattern(agentVersion, kvp.Key))
                {
                    // Kill takes priority over Block
                    if (matchedAction == null || string.Equals(kvp.Value.Action, "Kill", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedAction = kvp.Value.Action;
                        matchedPattern = kvp.Key;
                    }

                    // If we already found a Kill, no need to check more
                    if (string.Equals(matchedAction, "Kill", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }

            return matchedAction != null
                ? (true, matchedAction, matchedPattern)
                : (false, "Block", null);
        }

        /// <summary>
        /// Adds or updates a version block rule.
        /// </summary>
        public async Task BlockVersionAsync(string versionPattern, string action, string createdByEmail, string? reason = null)
        {
            // Normalize
            versionPattern = versionPattern.Trim();
            action = string.Equals(action, "Kill", StringComparison.OrdinalIgnoreCase) ? "Kill" : "Block";

            if (!IsValidPattern(versionPattern))
                throw new ArgumentException($"Invalid version pattern: '{versionPattern}'. Use formats like '1.*', '1.0.*', '1.0.30' (<=), or '=1.0.30' (exact).");

            await _securityRepo.BlockVersionAsync(versionPattern, action, createdByEmail, reason);

            // Update cache
            _cache[versionPattern] = new VersionBlockCacheEntry { Action = action };

            _logger.LogWarning(
                "Version {Action} rule added: Pattern={Pattern}, CreatedBy={CreatedBy}, Reason={Reason}",
                action, versionPattern, createdByEmail, reason);
        }

        /// <summary>
        /// Removes a version block rule.
        /// </summary>
        public async Task UnblockVersionAsync(string versionPattern)
        {
            versionPattern = versionPattern.Trim();

            await _securityRepo.UnblockVersionAsync(versionPattern);

            _cache.TryRemove(versionPattern, out _);

            _logger.LogInformation("Version block rule removed: Pattern={Pattern}", versionPattern);
        }

        /// <summary>
        /// Returns all active version block rules.
        /// </summary>
        public Task<List<BlockedVersionEntry>> GetBlockedVersionsAsync()
            => _securityRepo.GetBlockedVersionsAsync();

        // -----------------------------------------------------------------------
        // Version matching logic
        // -----------------------------------------------------------------------

        /// <summary>
        /// Checks if an agent version matches a block pattern.
        /// - "1.*"      → agent version starts with "1." (major match)
        /// - "1.0.*"    → agent version starts with "1.0." (major.minor match)
        /// - "1.0.30"   → agent version parsed as semver, matches if agentVersion &lt;= pattern
        /// - "=1.0.30"  → exact match, only version 1.0.30
        /// </summary>
        internal static bool VersionMatchesPattern(string agentVersion, string pattern)
        {
            if (string.IsNullOrEmpty(agentVersion) || string.IsNullOrEmpty(pattern))
                return false;

            // Exact match: "=1.0.30" matches only that specific version
            if (pattern.StartsWith("="))
            {
                var exactVersion = pattern.Substring(1);
                if (TryParseVersion(agentVersion, out var agentExact) && TryParseVersion(exactVersion, out var patternExact))
                    return CompareVersionParts(agentExact!, patternExact!) == 0;
                return string.Equals(agentVersion, exactVersion, StringComparison.OrdinalIgnoreCase);
            }

            // Wildcard patterns: prefix match
            if (pattern.EndsWith(".*"))
            {
                var prefix = pattern.Substring(0, pattern.Length - 1); // "1." or "1.0."
                return agentVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }

            // Version ceiling: <= comparison
            if (TryParseVersion(agentVersion, out var agentParts) && TryParseVersion(pattern, out var patternParts))
            {
                return CompareVersionParts(agentParts!, patternParts!) <= 0;
            }

            // Fallback: exact string match
            return string.Equals(agentVersion, pattern, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parses a version string like "1.0.30" into integer components.
        /// Ignores any suffix after a dash or plus (e.g. "1.0.30-beta").
        /// </summary>
        private static bool TryParseVersion(string version, out int[]? parts)
        {
            parts = null;
            if (string.IsNullOrEmpty(version)) return false;

            // Strip pre-release suffix (e.g. "1.0.30-beta+build123")
            var dashIndex = version.IndexOf('-');
            if (dashIndex >= 0) version = version.Substring(0, dashIndex);
            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0) version = version.Substring(0, plusIndex);

            var segments = version.Split('.');
            var parsed = new List<int>();

            foreach (var seg in segments)
            {
                if (int.TryParse(seg, out var n))
                    parsed.Add(n);
                else
                    return false;
            }

            if (parsed.Count == 0) return false;
            parts = parsed.ToArray();
            return true;
        }

        /// <summary>
        /// Compares two version part arrays. Returns &lt;0 if a &lt; b, 0 if equal, &gt;0 if a &gt; b.
        /// Missing parts are treated as 0 (e.g. "1.0" == "1.0.0").
        /// </summary>
        private static int CompareVersionParts(int[] a, int[] b)
        {
            var maxLen = Math.Max(a.Length, b.Length);
            for (int i = 0; i < maxLen; i++)
            {
                var av = i < a.Length ? a[i] : 0;
                var bv = i < b.Length ? b[i] : 0;
                if (av != bv) return av.CompareTo(bv);
            }
            return 0;
        }

        /// <summary>
        /// Validates that a pattern is well-formed.
        /// Valid: "1.*", "1.0.*", "1.0.30", "=1.0.30", etc.
        /// </summary>
        internal static bool IsValidPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;

            // Exact match prefix
            if (pattern.StartsWith("="))
                return TryParseVersion(pattern.Substring(1), out _);

            // Wildcard patterns
            if (pattern.EndsWith(".*"))
            {
                var prefix = pattern.Substring(0, pattern.Length - 2);
                // prefix must be a valid partial version (e.g. "1" or "1.0")
                return TryParseVersion(prefix, out _);
            }

            // Version ceiling
            return TryParseVersion(pattern, out _);
        }

        // -----------------------------------------------------------------------
        // Cache loading
        // -----------------------------------------------------------------------

        private async Task LoadBlockListAsync()
        {
            _loaded = true; // Mark before async to prevent parallel loads

            try
            {
                var entries = await _securityRepo.GetBlockedVersionsAsync();

                var freshCache = new ConcurrentDictionary<string, VersionBlockCacheEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    freshCache[entry.VersionPattern] = new VersionBlockCacheEntry
                    {
                        Action = entry.Action
                    };
                }

                // Atomically replace cache contents
                _cache.Clear();
                foreach (var kvp in freshCache)
                {
                    _cache[kvp.Key] = kvp.Value;
                }

                _lastLoadedUtc = DateTime.UtcNow;
                _logger.LogDebug("Loaded {Count} version block rules (refresh interval: {Interval}min)", _cache.Count, CacheRefreshInterval.TotalMinutes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load version block rules");
                // On error: if we had data before, keep it (stale > nothing). Only reset _loaded if we never loaded.
                if (_lastLoadedUtc == DateTime.MinValue)
                {
                    _loaded = false;
                }
                // Otherwise keep _loaded=true and _lastLoadedUtc as-is so we retry on next interval
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private class VersionBlockCacheEntry
        {
            public string Action { get; set; } = "Block";
        }
    }

    // Note: BlockedVersionEntry is now defined in AutopilotMonitor.Shared.DataAccess.IDeviceSecurityRepository
}
