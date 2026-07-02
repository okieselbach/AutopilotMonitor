using AutopilotMonitor.Functions.Middleware;

namespace AutopilotMonitor.Functions.Tests;

public class NoStoreCacheMiddlewareTests
{
    [Theory]
    // Exact-match credential / identity routes
    [InlineData("/api/auth/me")]
    [InlineData("/api/agent/config")]
    [InlineData("/api/agent/upload-url")]
    [InlineData("/api/bootstrap/config")]
    // Prefix-match routes — Bearer token (bootstrap/validate), tenant config, sessions
    [InlineData("/api/bootstrap/validate/ABC123")]
    [InlineData("/api/config/contoso-tenant-id")]
    [InlineData("/api/config/contoso-tenant-id/bootstrap-tokens")]
    [InlineData("/api/sessions/session-guid-here")]
    [InlineData("/api/sessions/session-guid-here/events")]
    // Session list + diagnostics-download surfaces — PII session payloads / proxied ZIPs
    [InlineData("/api/sessions")]
    [InlineData("/api/diagnostics/download-url")]
    [InlineData("/api/diagnostics/download-ticket")]   // short-lived signed download credential
    [InlineData("/api/diagnostics/download")]          // ticket-gated anonymous ZIP stream
    // /api/search/* — quick, sessions, sessions-by-event, sessions-by-cve (all return session PII)
    [InlineData("/api/search/quick")]
    [InlineData("/api/search/sessions")]
    [InlineData("/api/search/sessions-by-event")]
    [InlineData("/api/search/sessions-by-cve")]
    // GA cross-tenant raw + global PII surfaces
    [InlineData("/api/raw/sessions")]
    [InlineData("/api/raw/events")]
    [InlineData("/api/global/sessions")]
    [InlineData("/api/global/raw/sessions")]
    [InlineData("/api/global/raw/events")]
    [InlineData("/api/global/raw/tables/foo")]
    [InlineData("/api/global/search/sessions")]
    [InlineData("/api/global/search/sessions-by-cve")]
    [InlineData("/api/global/distress-reports")]
    [InlineData("/api/global/session-reports")]
    [InlineData("/api/global/session-reports/download-url")]   // 15-min SAS URL in response
    [InlineData("/api/global/session-reports/report-id/note")]
    // Case-insensitive
    [InlineData("/API/Auth/Me")]
    [InlineData("/api/Bootstrap/Validate/abc")]
    [InlineData("/API/Diagnostics/Download-Url")]
    [InlineData("/api/Global/Session-Reports/Download-Url")]
    public void IsSensitive_ReturnsTrue_ForCredentialBearingRoutes(string path)
    {
        Assert.True(NoStoreCacheMiddleware.IsSensitive(path),
            $"Expected '{path}' to be classified as sensitive.");
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/stats/platform")]
    [InlineData("/api/agent/telemetry")]
    [InlineData("/api/agent/register-session")]
    [InlineData("/api/auth/global-admins")]        // returns admin list, but no Bearer/SAS
    [InlineData("/api/auth/is-global-admin")]
    [InlineData("/api/rules")]
    [InlineData("/api/rules/gather")]
    [InlineData("/api/rules/analyze")]
    [InlineData("/api/notifications")]             // tenant bell — no SAS / token
    [InlineData("/api/audit/logs")]                // not in scope: see middleware allowlist commentary
    [InlineData("/")]
    [InlineData("")]
    public void IsSensitive_ReturnsFalse_ForNonCredentialRoutes(string path)
    {
        Assert.False(NoStoreCacheMiddleware.IsSensitive(path),
            $"Expected '{path}' to NOT be classified as sensitive (would over-stamp no-store).");
    }

    [Fact]
    public void IsSensitive_NullPath_ReturnsFalse()
    {
        Assert.False(NoStoreCacheMiddleware.IsSensitive(null!));
    }
}
