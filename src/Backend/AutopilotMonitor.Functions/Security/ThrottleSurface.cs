namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// The budget class of an authenticated (JWT) caller for per-user rate limiting. Derived ONLY
    /// from signed token claims (<c>appidacr</c>/<c>azpacr</c>, <c>idtyp</c>) — never from a request
    /// header: any token holder can add or omit a header, and the abuse direction is always "into the
    /// generous budget". In production the portal SPA, the MCP server and the API share one app
    /// registration, so the client id cannot tell them apart; how the client authenticated can.
    /// </summary>
    public enum ThrottleSurface
    {
        /// <summary>
        /// Interactive session of a public client (SPA with PKCE, <c>appidacr = 0</c>): the portal.
        /// Generous budget — a cost cap per account, not a security boundary (anyone with an account
        /// can mint such a token in a browser and script it).
        /// </summary>
        Portal,

        /// <summary>
        /// Confidential client (client secret or certificate: the MCP server acting for a user, a
        /// server-side integration) or an app-only principal; also the fail-closed default when the
        /// claim is missing. The tenant override and the edition floor apply here.
        /// </summary>
        Integration
    }

    public static class ThrottleSurfaceExtensions
    {
        /// <summary>Stable lower-case name used in bucket keys and the request dimension.</summary>
        public static string ToDimension(this ThrottleSurface surface)
            => surface == ThrottleSurface.Portal ? "portal" : "integration";
    }
}
