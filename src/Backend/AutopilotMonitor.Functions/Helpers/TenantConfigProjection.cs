using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Server-side keep-list projection for the paginated <c>GET /api/config/all</c> surface.
    /// Only non-sensitive identity / lifecycle / plan fields are ever emitted — secrets
    /// (webhook / SAS / branding URLs, allow-lists) can never leave the backend even if a
    /// caller requests them via <c>fields=</c>, because the requested set is intersected with
    /// this keep-list (unknown keys are silently dropped, never widened). <c>tenantId</c> is
    /// always included: the endpoint's whole purpose is tenant-ID discovery.
    /// </summary>
    public static class TenantConfigProjection
    {
        // Ordered keep-list: JSON key (camelCase — the wire contract the web + MCP already
        // consume) -> value selector. This is the ONLY set of fields that can reach the wire.
        private static readonly (string Key, Func<TenantConfiguration, object?> Value)[] SafeFields =
        {
            ("tenantId",          c => c.TenantId),
            ("domainName",        c => c.DomainName),
            ("planTier",          c => c.PlanTier),
            ("trialExpiresUtc",   c => c.TrialExpiresUtc),
            ("trialConsumed",     c => c.TrialConsumed),
            ("disabled",          c => c.Disabled),
            ("disabledReason",    c => c.DisabledReason),
            ("onboardedAt",       c => c.OnboardedAt),
            ("onboardedBy",       c => c.OnboardedBy),
            ("lastUpdated",       c => c.LastUpdated),
            ("dataRetentionDays", c => c.DataRetentionDays),
        };

        /// <summary>
        /// Parse a comma-separated field list into a case-insensitive set, or null when no
        /// usable subset was requested (→ emit all safe fields). Keys outside the keep-list
        /// are kept in the set but simply never match a real field in <see cref="Project"/>.
        /// </summary>
        public static HashSet<string>? ParseFields(string? fields)
        {
            if (string.IsNullOrWhiteSpace(fields)) return null;
            var requested = fields
                .Split(',')
                .Select(f => f.Trim())
                .Where(f => f.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return requested.Count > 0 ? requested : null;
        }

        /// <summary>
        /// Project one tenant to a { jsonKey -> value } map containing only the requested
        /// safe fields (or all safe fields when <paramref name="requested"/> is null).
        /// <c>tenantId</c> is always present regardless of the request.
        /// </summary>
        public static Dictionary<string, object?> Project(TenantConfiguration config, HashSet<string>? requested)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var result = new Dictionary<string, object?>(SafeFields.Length);
            foreach (var (key, value) in SafeFields)
            {
                if (requested == null || key == "tenantId" || requested.Contains(key))
                    result[key] = value(config);
            }
            return result;
        }

        /// <summary>Convenience: parse + project a sequence in one call.</summary>
        public static List<Dictionary<string, object?>> ProjectAll(
            IEnumerable<TenantConfiguration> configs, string? fields)
        {
            var requested = ParseFields(fields);
            return configs.Select(c => Project(c, requested)).ToList();
        }
    }
}
