using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;

namespace AutopilotMonitor.Functions.Security;

/// <summary>
/// Client-app binding for delegated (user) tokens. Entra issues a token for this API to any application a
/// tenant consented to; the token names that application in <c>azp</c> (v2.0) / <c>appid</c> (v1.0). A token
/// obtained by one of the platform's own registrations (the audience trust set: primary, legacy and
/// additional client ids) is the portal or the MCP server and passes untouched. Any other client — a
/// self-hosted AI client using on-behalf-of, an in-house tool — is "foreign": it is measured on every
/// request and, while enforcement is on, admitted only when that application is an enabled member of the
/// caller's tenant (the <c>app:&lt;client-id&gt;</c> row a service principal is granted under), with the
/// caller capped like an application principal (<see cref="AdminIdentity.IsCapped"/>).
/// </summary>
public static class ClientAppBinding
{
    /// <summary><c>FunctionContext.Items</c> key: the foreign client's application id (absent when the token names none).</summary>
    public const string ClientAppIdItemKey = "ClientAppId";

    /// <summary><c>FunctionContext.Items</c> key: one of <see cref="Outcomes"/> — set only for a foreign client.</summary>
    public const string OutcomeItemKey = "ClientAppBinding";

    /// <summary>
    /// Authentication type of the marker identity added to an admitted foreign-client principal. A token
    /// cannot produce it: every token-derived identity carries the handler's authentication type, so the
    /// marker is only ever the middleware's own statement.
    /// </summary>
    public const string CappedAuthenticationType = "AutopilotMonitor.ClientAppBinding";

    /// <summary>Claim on the marker identity naming the admitted client application.</summary>
    public const string ClientAppClaimType = "client_app";

    public static class Outcomes
    {
        /// <summary>The application is an enabled member of the caller's tenant.</summary>
        public const string Registered = "registered";

        /// <summary>The application is not a member of the caller's tenant, or its row is disabled.</summary>
        public const string Unregistered = "unregistered";

        /// <summary>The token names no client application at all (never seen from Entra; fail-closed when enforced).</summary>
        public const string Unidentified = "unidentified";
    }

    /// <summary>
    /// True when <paramref name="principal"/> is a delegated token obtained by a client outside
    /// <paramref name="trustedClientIds"/>. App-only tokens are never foreign here — they have their own gate
    /// (<c>access_as_application</c>) and are keyed on their own member row. <paramref name="clientAppId"/> is
    /// the token's client id (lowercase) or null when it names none.
    /// </summary>
    public static bool IsForeignDelegatedToken(
        ClaimsPrincipal principal, IReadOnlyCollection<string> trustedClientIds, out string? clientAppId)
    {
        clientAppId = null;
        if (principal.IsApplicationPrincipal())
            return false;

        clientAppId = principal.GetApplicationId();
        return clientAppId == null
               || !trustedClientIds.Contains(clientAppId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Marks an admitted foreign-client principal so every role resolution applies the application caps.</summary>
    public static void MarkCapped(ClaimsPrincipal principal, string clientAppId)
        => principal.AddIdentity(new ClaimsIdentity(
            new[] { new Claim(ClientAppClaimType, clientAppId) }, CappedAuthenticationType));

    /// <summary>True when the middleware admitted this principal as a foreign client (see <see cref="MarkCapped"/>).</summary>
    public static bool IsCapped(ClaimsPrincipal principal)
        => principal.Identities.Any(i => string.Equals(i.AuthenticationType, CappedAuthenticationType, StringComparison.Ordinal));

    /// <summary>The 403 text for a refused foreign client: names the application and the one fix.</summary>
    public static string RefusalMessage(string? clientAppId)
        => clientAppId == null
            ? "This token names no client application and is not accepted. Sign in through the Autopilot Monitor portal or a supported MCP client."
            : $"The application {clientAppId} that obtained this token is not registered in your organization. " +
              "A Tenant Admin can add it under Settings > Access Management as a service principal; its users then have read-only (Viewer) access.";
}
