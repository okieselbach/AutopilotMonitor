using System.Net;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Shared handling of a Graph 401/403 across the device validators.
    /// <para>
    /// The app-only Graph token is cached per Function instance for up to 55 minutes
    /// (<see cref="GraphTokenService"/>). When a tenant grants admin consent, only the instance that
    /// served the consent flow invalidates its cache; every other instance keeps a token that was
    /// minted BEFORE the grant and therefore carries no <c>roles</c>. Graph answers 403 for that
    /// token forever, and a validator that classifies the 403 as transient parks the agent in a
    /// 503 Retry-After loop until the token expires (field case 2026-09-02: 45 minutes, 312 rejected
    /// ingest calls of one freshly onboarded tenant). A validator that classifies it as definitive is
    /// worse: the agent treats a 403 as "device not registered" and shuts down after a few in a row.
    /// </para>
    /// <para>
    /// The fix is per request and needs no cross-instance channel: on the FIRST attempt an auth
    /// failure drops the tenant's cached token, the validator reports the failure as transient, and
    /// its existing retry loop mints a fresh token for the second attempt. A 401/403 on the second
    /// attempt is then a real permission gap (consent missing or not yet propagated); each validator
    /// keeps its own semantics for that case, logged through <see cref="LogPermissionMissing"/> so
    /// the operator sees the tenant.
    /// </para>
    /// </summary>
    internal static class GraphAuthFailure
    {
        /// <summary>
        /// Retry-After handed to the agent when a fresh token still gets 401/403: a consent gap does
        /// not close in 30 seconds, and a quarter of the retry volume is enough to notice when it does.
        /// </summary>
        public const int PermissionMissingRetryAfterSeconds = 120;

        public static bool IsAuthFailure(HttpStatusCode status)
            => status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden;

        /// <summary>
        /// On the first attempt an auth failure is treated as a possibly stale token: the tenant's
        /// cached token is invalidated so the retry mints a fresh one. Returns true when the caller
        /// must classify the failure as transient (so its loop retries) instead of applying its
        /// definitive 401/403 handling. Never invalidates on a later attempt or on any other status.
        /// </summary>
        public static bool TryRecoverStaleToken(
            GraphTokenService tokenService, ILogger logger, string validator, string tenantId, HttpStatusCode status, int attempt)
        {
            if (attempt != 1 || !IsAuthFailure(status))
                return false;

            tokenService.InvalidateTenant(tenantId);
            logger.LogWarning(
                "{Validator}: Graph auth failure {StatusCode} for tenant {TenantId} on attempt 1 — token invalidated, retrying with a fresh token",
                validator, (int)status, tenantId);
            return true;
        }

        /// <summary>
        /// Stable marker for the operator: the fresh token still lacks the permission. The caller
        /// decides what the agent sees (transient 503 or the validator's definitive negative).
        /// </summary>
        public static void LogPermissionMissing(ILogger logger, string validator, string tenantId, HttpStatusCode status)
            => logger.LogWarning(
                "{Validator}: Graph permission missing for tenant {TenantId} after fresh token (status {StatusCode})",
                validator, tenantId, (int)status);
    }
}
