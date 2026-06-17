using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Security;

/// <summary>
/// The three states a TenantAdmins table row can be in, as seen by the effective-role resolver.
/// The distinction matters because a disabled row is an explicit manual deny that must suppress
/// the Entra app-role claim — collapsing it to "no row" would let a disabled user be re-authorized
/// through their group/app-role assignment.
/// </summary>
public enum TableMemberState
{
    /// <summary>No TenantAdmins row exists for this user.</summary>
    NotPresent,

    /// <summary>A row exists but is disabled — explicit deny, never falls back to the claim.</summary>
    Disabled,

    /// <summary>An enabled row exists; its role is authoritative.</summary>
    Enabled
}

/// <summary>
/// Resolves a tenant member role from Entra ID app-role claims (the "roles" claim emitted by
/// the application's Enterprise App) and reconciles it with the TenantAdmins table.
///
/// Resolution order is TABLE FIRST, claim as fallback: a manual TenantAdmins entry always wins
/// over an Entra group/app-role assignment. An enabled row supplies the role; a <em>disabled</em>
/// row is an explicit deny that suppresses the claim entirely (so a disabled user cannot be
/// re-authorized via their app-role assignment). Only when no row exists at all is the claim
/// consulted. This lets an admin override a claim-derived Operator (e.g. to grant
/// <see cref="MemberRoleInfo.CanManageBootstrapTokens"/>, or to revoke access) by adding an
/// explicit table row for that user.
///
/// Only Admin and Operator are mappable from claims; Viewer and the platform-wide GlobalAdmin
/// role are intentionally never derived from claims.
/// </summary>
public static class EntraAppRoleResolver
{
    /// <summary>
    /// Maps raw Entra app-role values to a <see cref="MemberRoleInfo"/>. Admin outranks Operator
    /// when both are present. Returns null when no mappable role exists.
    /// A claim-derived Admin implicitly gets <see cref="MemberRoleInfo.CanManageBootstrapTokens"/>;
    /// a claim Operator does not (granular bootstrap permission is a table-only override).
    /// </summary>
    public static MemberRoleInfo? MapClaimRole(IEnumerable<string>? appRoles)
    {
        if (appRoles == null)
            return null;

        var roles = appRoles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();

        if (roles.Any(r => string.Equals(r, Constants.TenantRoles.Admin, StringComparison.OrdinalIgnoreCase)))
            return new MemberRoleInfo { Role = Constants.TenantRoles.Admin, CanManageBootstrapTokens = true };

        if (roles.Any(r => string.Equals(r, Constants.TenantRoles.Operator, StringComparison.OrdinalIgnoreCase)))
            return new MemberRoleInfo { Role = Constants.TenantRoles.Operator, CanManageBootstrapTokens = false };

        return null;
    }

    /// <summary>
    /// Returns the effective member role given the table state and any Entra app-role claim.
    /// <list type="bullet">
    ///   <item><see cref="TableMemberState.Enabled"/> → the table role (always wins).</item>
    ///   <item><see cref="TableMemberState.Disabled"/> → null (explicit deny; claim ignored).</item>
    ///   <item><see cref="TableMemberState.NotPresent"/> → the claim-derived role, but only when
    ///         <paramref name="appRolesEnabled"/> is true for the tenant; otherwise null.</item>
    /// </list>
    /// </summary>
    public static MemberRoleInfo? Resolve(
        TableMemberState tableState, MemberRoleInfo? tableRole,
        IEnumerable<string>? appRoles, bool appRolesEnabled)
    {
        if (tableState == TableMemberState.Enabled)
            return tableRole;

        if (tableState == TableMemberState.Disabled)
            return null; // explicit manual deny — never fall back to the claim

        return appRolesEnabled ? MapClaimRole(appRoles) : null;
    }
}
