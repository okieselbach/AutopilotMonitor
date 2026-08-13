/**
 * Shared predicates for "does this user get the read-oriented portal surface
 * (dashboard, session list, monitoring nav) or the member-only Progress Portal?".
 *
 * Loose structural subset of the AuthContext UserInfo shape so callers can pass
 * the context user, hook-local user shapes (optional fields), or test doubles.
 */
export interface TenantScopeUser {
  isTenantAdmin?: boolean;
  isGlobalAdmin?: boolean;
  isGlobalReader?: boolean;
  isDelegated?: boolean;
  role?: string | null;
}

/** The three assignable tenant roles (mirrors Constants.TenantRoles in the backend). */
const TENANT_ROLES = ["Admin", "Operator", "Viewer"];

/**
 * True when the user holds a role in their OWN tenant (Admin/Operator/Viewer —
 * any resolved tenant role) or a platform scope (Global Admin / Global Reader).
 * Deliberately EXCLUDES delegated ("MSP") scope: routing uses this to decide
 * whether a delegated user has an own-tenant home or should land on /fleet.
 */
export function hasOwnTenantOrPlatformRole(
  user: TenantScopeUser | null | undefined
): boolean {
  if (!user) return false;
  return !!(
    user.isTenantAdmin ||
    user.isGlobalAdmin ||
    user.isGlobalReader ||
    (user.role != null && TENANT_ROLES.includes(user.role))
  );
}

/**
 * True when the user can read tenant telemetry beyond their own device sessions:
 * any resolved tenant role (Admin/Operator/Viewer), platform scope (Global
 * Admin / Global Reader), or delegated ("MSP") scope. Users WITHOUT this scope
 * are "regular" members and belong on the Progress Portal.
 *
 * Read-only enforcement for Viewer/GlobalReader/delegated users happens inside
 * the pages (canEdit-style flags) and in the backend policy tiers — never by
 * denying reachability here.
 */
export function hasTenantReadScope(
  user: TenantScopeUser | null | undefined
): boolean {
  if (!user) return false;
  return hasOwnTenantOrPlatformRole(user) || !!user.isDelegated;
}
