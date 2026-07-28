using System;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates the values interpolated into the OOBE bootstrap PowerShell script.
    ///
    /// SECURITY-CRITICAL: All validated values are interpolated into a PowerShell
    /// script that runs as SYSTEM during Windows OOBE (see OobeBootstrapScriptGenerator).
    /// Every rule below was chosen to block PS metacharacters ($, backticks, ", ', #>,
    /// newline) so that even if the second-line defense (single-quoted PS string
    /// literals) is accidentally loosened, the validator still prevents injection.
    /// Ported from the portal's utils/bootstrapValidation.ts when the /go route moved
    /// into the backend; kept even though values are now produced in-process —
    /// defense-in-depth against tampered table data or a service bug. Do NOT relax any
    /// rule without re-reviewing OobeBootstrapScriptGenerator end-to-end.
    /// </summary>
    public static class BootstrapScriptValueValidator
    {
        public enum ValidationFailure
        {
            TenantId,
            Token,
            AgentDownloadUrl,
            ExpiresAt,
        }

        public readonly record struct ValidatedValues(
            string TenantId,
            string Token,
            string AgentDownloadUrl,
            DateTime ExpiresAtUtc);

        // Byte-identical to AGENT_PATH_RE in the portal's bootstrapValidation.ts:
        // fixed /agent/ prefix, no leading dot, ≤80-char filename, .zip suffix.
        private static readonly Regex AgentPathRegex = new(
            @"^/agent/[A-Za-z0-9_-][A-Za-z0-9._-]{0,79}\.zip$",
            RegexOptions.Compiled);

        private static readonly Regex PrintableAsciiRegex = new(
            @"^[\x20-\x7E]+$",
            RegexOptions.Compiled);

        // Mirror of AGENT_DOWNLOAD_HOSTNAMES in the portal's utils/config.ts —
        // derived from the Constants registry, never repeated as literals.
        private static readonly string[] AllowedDownloadHosts =
        {
            new Uri(Constants.AgentDownloadBaseUrl).Host,
            new Uri(Constants.AgentBlobBaseUrl).Host,
        };

        private static readonly TimeSpan MaxExpiryWindow = TimeSpan.FromDays(14);

        public static bool TryValidate(
            string tenantId,
            string token,
            string agentDownloadUrl,
            DateTime expiresAtUtc,
            out ValidatedValues values,
            out ValidationFailure failure)
        {
            values = default;

            if (!IsGuidExact(tenantId))
            {
                failure = ValidationFailure.TenantId;
                return false;
            }

            if (!IsGuidExact(token))
            {
                failure = ValidationFailure.Token;
                return false;
            }

            if (!IsValidAgentDownloadUrl(agentDownloadUrl))
            {
                failure = ValidationFailure.AgentDownloadUrl;
                return false;
            }

            var nowUtc = DateTime.UtcNow;
            if (expiresAtUtc <= nowUtc || expiresAtUtc > nowUtc + MaxExpiryWindow)
            {
                failure = ValidationFailure.ExpiresAt;
                return false;
            }

            values = new ValidatedValues(tenantId, token, agentDownloadUrl, expiresAtUtc);
            failure = default;
            return true;
        }

        /// <summary>
        /// Exactly 36 chars in canonical "D" format (8-4-4-4-12 hex with hyphens).
        /// Guid.TryParseExact alone would accept surrounding whitespace via trim —
        /// the length check keeps the character class airtight.
        /// </summary>
        private static bool IsGuidExact(string value)
        {
            return value is { Length: 36 }
                && Guid.TryParseExact(value, "D", out _);
        }

        private static bool IsValidAgentDownloadUrl(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 256)
                return false;
            if (!PrintableAsciiRegex.IsMatch(value))
                return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
                return false;
            if (url.Scheme != Uri.UriSchemeHttps)
                return false;
            if (!AllowedDownloadHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(url.UserInfo))
                return false;
            if (!url.IsDefaultPort)
                return false;
            if (!string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
                return false;
            if (!AgentPathRegex.IsMatch(url.AbsolutePath))
                return false;
            return true;
        }
    }
}
