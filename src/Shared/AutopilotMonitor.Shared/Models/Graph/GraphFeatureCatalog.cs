using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Graph
{
    /// <summary>
    /// Maps customer-facing "feature" identifiers (the ones surfaced in the Admin UI and accepted
    /// by the customer-side grant script) to the set of Microsoft Graph application permissions
    /// required to enable them. Customers never have to know permission strings: they pick
    /// features, we own the mapping.
    /// </summary>
    public static class GraphFeatureCatalog
    {
        /// <summary>Resolves Intune Platform Script + Remediation Script display names in session timelines.</summary>
        public const string FeatureScriptDisplayNames = "ScriptDisplayNames";

        /// <summary>Validates Windows 365 Cloud PCs via the service-side Cloud PC inventory (cert-CN bound).</summary>
        public const string FeatureW365CloudPcValidation = "W365CloudPcValidation";

        // Backed by a plain dictionary (netstandard2.0 + C# 9, no Immutable types).
        private static readonly Dictionary<string, string[]> _byFeature
            = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureScriptDisplayNames] = new[] { GraphAppPermissions.DeviceManagementScriptsReadAll },
                [FeatureW365CloudPcValidation] = new[] { GraphAppPermissions.CloudPCReadAll },
            };

        /// <summary>All registered feature identifiers.</summary>
        public static IReadOnlyCollection<string> Features => _byFeature.Keys;

        /// <summary>Returns the Graph permissions a feature requires, or empty when unknown.</summary>
        public static IReadOnlyList<string> RequiredPermissions(string feature)
        {
            if (string.IsNullOrWhiteSpace(feature)) return Array.Empty<string>();
            return _byFeature.TryGetValue(feature, out var perms) ? perms : Array.Empty<string>();
        }

        /// <summary>
        /// True when EVERY permission a feature needs is present in <paramref name="grantedRoles"/>.
        /// Accepts any role-collection shape — <see cref="ISet{T}"/>, <c>IReadOnlySet&lt;T&gt;</c>
        /// (on TFMs that have it), <see cref="HashSet{T}"/>, plain array — without forcing the
        /// caller into an unsafe down-cast. Comparison is case-insensitive: Azure AD may issue
        /// role names with slightly different casing across tokens.
        /// </summary>
        public static bool IsFeatureGranted(string feature, IEnumerable<string>? grantedRoles)
        {
            if (grantedRoles == null) return false;
            var required = RequiredPermissions(feature);
            if (required.Count == 0) return false;

            // Materialise once into a case-insensitive set. Required is tiny (<5 entries per
            // feature today) so the allocation is irrelevant compared to the call-site savings.
            var rolesSet = grantedRoles as HashSet<string>;
            if (rolesSet == null || !ReferenceEquals(rolesSet.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                rolesSet = new HashSet<string>(grantedRoles, StringComparer.OrdinalIgnoreCase);
            }
            for (int i = 0; i < required.Count; i++)
            {
                if (!rolesSet.Contains(required[i])) return false;
            }
            return true;
        }
    }
}
