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
  canManageBootstrapTokens?: boolean;
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

/**
 * True when Admin Mode may take effect for the user: the tenant Admin or a Global Admin, the roles
 * the backend's TenantAdminOrGA tier admits for the actions behind it (delete, mark succeeded or
 * failed). The toggle shows for exactly these users, and a stored "on" from another account in the
 * same browser stays inert for everyone else.
 */
export function canUseAdminMode(user: TenantScopeUser | null | undefined): boolean {
  if (!user) return false;
  return !!(user.isGlobalAdmin || user.isTenantAdmin || user.role === "Admin");
}

/**
 * True when the user may run Admin Mode actions on a session of `sessionTenantId`: a Global Admin
 * on any tenant, the tenant Admin only on their own (`ownTenantId`). A delegated or Global Reader
 * view of a foreign tenant gets no write control, because the backend refuses it there.
 */
export function canAdministerTenant(
  user: TenantScopeUser | null | undefined,
  sessionTenantId: string | null | undefined,
  ownTenantId: string | null | undefined
): boolean {
  if (!user) return false;
  if (user.isGlobalAdmin) return true;
  if (!canUseAdminMode(user) || !sessionTenantId || !ownTenantId) return false;
  return sessionTenantId.toLowerCase() === ownTenantId.toLowerCase();
}

/**
 * True when the user may list and manage their OWN tenant's bootstrap sessions — the client
 * mirror of the backend's BootstrapManagerOrGA tier: Global Admin, the tenant Admin, or an
 * Operator granted "manage bootstrap tokens". It is a write tier, so neither the read-only
 * Global Reader nor a delegated grant is admitted.
 */
export function canManageBootstrapSessions(
  user: TenantScopeUser | null | undefined
): boolean {
  if (!user) return false;
  return !!(
    user.isGlobalAdmin ||
    user.isTenantAdmin ||
    user.role === "Admin" ||
    (user.role === "Operator" && user.canManageBootstrapTokens)
  );
}
