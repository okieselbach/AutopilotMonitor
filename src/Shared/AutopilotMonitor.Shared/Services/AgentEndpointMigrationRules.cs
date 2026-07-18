using System;

namespace AutopilotMonitor.Shared.Services
{
    /// <summary>
    /// Validation rules for the config-channel agent endpoint migration target
    /// (<c>AgentConfigResponse.MigrateToApiBaseUrl</c>). Single source of truth used by BOTH
    /// sides — the backend refuses to serve an invalid target, the agent refuses to honour
    /// one — so a misconfigured admin value or a tampered response can never re-home agents
    /// to an arbitrary host (defence in depth, same philosophy as the kill-switch hardening).
    /// </summary>
    public static class AgentEndpointMigrationRules
    {
        /// <summary>
        /// Host suffixes a migration target may resolve to. Deliberately narrow: the platform
        /// only ever runs its API on Azure Functions default hostnames (a custom domain in
        /// front of the API is impossible — mTLS client-cert auth does not survive a proxy,
        /// and Flex Consumption custom-domain TLS is broken). Extend here if that ever changes;
        /// old agent builds keep their compiled-in list, so plan one release of lead time.
        /// </summary>
        public static readonly string[] AllowedHostSuffixes = { ".azurewebsites.net" };

        /// <summary>Defensive cap — a legitimate Functions hostname URL is far shorter.</summary>
        public const int MaxTargetLength = 200;

        /// <summary>
        /// Validates <paramref name="candidate"/> as a migration target and returns the
        /// normalized base URL (<c>https://{lowercase-host}</c>, no trailing slash) on success.
        /// Rules: absolute https URI, default port, no userinfo/path/query/fragment, host
        /// matching <see cref="AllowedHostSuffixes"/>.
        /// </summary>
        public static bool TryNormalizeTarget(string candidate, out string normalized)
        {
            normalized = null;

            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxTargetLength)
                return false;

            if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!uri.IsDefaultPort)
                return false;

            if (!string.IsNullOrEmpty(uri.UserInfo))
                return false;

            // Base URL only — endpoint paths are appended by the agent's clients.
            if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return false;

            var host = uri.Host;
            var allowed = false;
            foreach (var suffix in AllowedHostSuffixes)
            {
                // Suffix match must sit on a label boundary and leave a non-empty prefix:
                // "evil-azurewebsites.net" and ".azurewebsites.net" itself are both rejected.
                if (host.Length > suffix.Length
                    && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }
            if (!allowed)
                return false;

            normalized = "https://" + host.ToLowerInvariant();
            return true;
        }

        /// <summary>
        /// <c>true</c> when <paramref name="candidate"/> is a valid target that actually
        /// differs from <paramref name="currentBaseUrl"/> (both compared in normalized form —
        /// serving the agent its own current URL is a no-op, not a migration).
        /// </summary>
        public static bool IsEffectiveMigration(string candidate, string currentBaseUrl, out string normalizedTarget)
        {
            normalizedTarget = null;

            if (!TryNormalizeTarget(candidate, out var target))
                return false;

            // The current base URL may not itself pass the allowlist (dev --api-url override);
            // fall back to a plain trim-and-lower comparison in that case.
            var currentNormalized = TryNormalizeTarget(currentBaseUrl, out var normCurrent)
                ? normCurrent
                : (currentBaseUrl ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

            if (string.Equals(target, currentNormalized, StringComparison.OrdinalIgnoreCase))
                return false;

            normalizedTarget = target;
            return true;
        }
    }
}
