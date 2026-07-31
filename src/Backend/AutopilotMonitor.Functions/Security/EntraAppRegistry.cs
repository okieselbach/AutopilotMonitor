using System;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Credentials of one Entra app registration the platform operates under.
    /// <see cref="IsLegacy"/> marks the pre-migration app (gktatooine.net home tenant).
    /// </summary>
    public sealed record EntraAppCredentials(string ClientId, string? ClientSecret, bool IsLegacy);

    /// <summary>
    /// Single source for "which Entra app registration acts on behalf of this tenant" during the
    /// dual app-registration parallel window (legacy gktatooine app ∥ new C4A8 app).
    ///
    /// Config axes:
    ///  - <c>EntraId:ClientId</c> / <c>EntraId:ClientSecret</c> — the PRIMARY app (post-swap: the new C4A8 app).
    ///  - <c>EntraId:LegacyClientId</c> / <c>EntraId:LegacyClientSecret</c> — the pre-migration app.
    ///
    /// Resolution contract (<see cref="ResolveForTenant"/>):
    ///  - Legacy app not configured ⇒ ALWAYS primary. This makes deploying this code a no-op until
    ///    the operator performs the config swap (primary→new + legacy→old) — before that swap the
    ///    primary IS the old app and behaviour is byte-identical to today.
    ///  - <see cref="TenantConfiguration.HomedAppClientId"/> null ⇒ legacy (the invariant: every
    ///    tenant onboarded before the migration has no homing value and stays on the old app).
    ///  - Equal to the primary/legacy client id ⇒ that app. Unknown value ⇒ primary + warning
    ///    (fail toward the app new consents land on; an unknown id is an operator typo).
    ///
    /// Token VALIDATION (inbound audiences) is not decided here — the middleware trusts primary +
    /// legacy + EntraId:AdditionalClientIds simultaneously; this registry only picks the app used
    /// for OUTBOUND artifacts: Graph client-credential tokens, admin-consent URLs, and the client
    /// id surfaced to tenant admins.
    /// </summary>
    public class EntraAppRegistry
    {
        private readonly ILogger<EntraAppRegistry> _logger;
        private readonly EntraAppCredentials _primary;
        private readonly EntraAppCredentials? _legacy;
        private readonly string? _primaryClientIdNormalized;

        public EntraAppRegistry(IConfiguration configuration, ILogger<EntraAppRegistry> logger)
        {
            _logger = logger;

            // Primary stays verbatim (same contract as AuthenticationMiddleware: its handling is
            // unchanged and never validated away). Normalized form is only used for comparisons.
            var primaryClientId = configuration["EntraId:ClientId"] ?? string.Empty;
            _primary = new EntraAppCredentials(primaryClientId, configuration["EntraId:ClientSecret"], IsLegacy: false);
            _primaryClientIdNormalized = NormalizeClientId(primaryClientId);

            var legacyClientIdRaw = configuration["EntraId:LegacyClientId"];
            var legacyClientId = NormalizeClientId(legacyClientIdRaw);
            if (legacyClientId != null)
            {
                _legacy = new EntraAppCredentials(legacyClientId, configuration["EntraId:LegacyClientSecret"], IsLegacy: true);
            }
            else if (!string.IsNullOrWhiteSpace(legacyClientIdRaw))
            {
                // Malformed (non-GUID) legacy id: treat as unset (fail toward primary-only, i.e.
                // pre-swap behaviour) but make the misconfiguration operator-visible — a typo here
                // would otherwise surface as "legacy tenants lost Graph/consent" with no clue.
                _logger.LogWarning(
                    "EntraId:LegacyClientId is not a valid GUID — ignoring the legacy app registration: {Entry}",
                    AuthenticationMiddleware.TruncateForLog(legacyClientIdRaw.Trim()));
            }
        }

        /// <summary>The primary app registration (post-swap: the new C4A8 app).</summary>
        public EntraAppCredentials Primary => _primary;

        /// <summary>The legacy app registration, or null while the config swap hasn't happened.</summary>
        public EntraAppCredentials? Legacy => _legacy;

        /// <summary>True once both app registrations are configured (the parallel window is active).</summary>
        public bool LegacyConfigured => _legacy != null;

        /// <summary>
        /// Picks the app registration that acts on behalf of the given tenant (Graph
        /// client-credential tokens, admin-consent URLs, surfaced client id).
        /// </summary>
        public virtual EntraAppCredentials ResolveForTenant(TenantConfiguration? tenantConfig)
        {
            if (_legacy == null)
            {
                return _primary;
            }

            var homed = NormalizeClientId(tenantConfig?.HomedAppClientId);
            if (homed == null)
            {
                // Null homing = onboarded before the migration ⇒ legacy app.
                return _legacy;
            }
            if (homed.Equals(_primaryClientIdNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return _primary;
            }
            if (homed.Equals(_legacy.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                return _legacy;
            }

            _logger.LogWarning(
                "Tenant {TenantId} has unknown HomedAppClientId {HomedAppClientId} — falling back to the primary app registration",
                tenantConfig?.TenantId, homed);
            return _primary;
        }

        /// <summary>True when the given client id (bare or api://-prefixed) is the primary app's.</summary>
        public virtual bool IsPrimary(string? clientIdOrAudience)
        {
            var normalized = NormalizeAudience(clientIdOrAudience);
            return normalized != null
                && normalized.Equals(_primaryClientIdNormalized, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes a token audience to the canonical lowercase dashed client id: strips an
        /// optional <c>api://</c> prefix and requires a GUID. Null for anything else — a Graph or
        /// unknown audience must never be persisted as app-reg provenance.
        /// </summary>
        public static string? NormalizeAudience(string? audience)
        {
            if (string.IsNullOrWhiteSpace(audience)) return null;
            var value = audience.Trim();
            const string apiPrefix = "api://";
            if (value.StartsWith(apiPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(apiPrefix.Length);
            }
            return NormalizeClientId(value);
        }

        /// <summary>Canonical lowercase dashed GUID form, or null when not a GUID.</summary>
        public static string? NormalizeClientId(string? clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return null;
            return Guid.TryParse(clientId.Trim(), out var parsed) ? parsed.ToString("D") : null;
        }
    }
}
