import { hasOwnTenantOrPlatformRole, type TenantScopeUser } from "./tenantScope";

/**
 * Client mirror of the SignalR join rules the backend decides from the caller's HOME-tenant
 * standing alone (SignalRGroupHelper + SignalRAddToGroupFunction). A join the backend refuses by
 * rule is not sent: it only ever produced a 4xx row and an api_request_failed event per page visit.
 *
 * Cross-tenant joins are deliberately NOT decided here, exactly as the backend helpers leave them
 * to the cross-tenant admission (platform scope, or a delegated grant over the group's tenant).
 * "Member" is hasOwnTenantOrPlatformRole, not hasTenantReadScope: a delegated admin without an
 * own-tenant role reads the managed tenants but is refused its home tenant's member groups.
 */

/** The caller's standing as auth/me reports it; `tenantId` is the HOME tenant. */
export interface GroupJoinCaller extends TenantScopeUser {
  tenantId: string;
}

const NOTIFY_MEMBER_SUFFIX = "-notify-member";
const NOTIFY_ADMIN_SUFFIX = "-notify-admin";
const GLOBAL_ADMINS_GROUP = "global-admins";

/** The tenant a group belongs to, parsed like SignalRGroupHelper.ExtractTenantIdFromGroupName. */
export function groupTenantId(groupName: string): string | null {
  if (groupName.startsWith("session-")) {
    // "session-{tenantId}-{sessionId}": the tenant GUID is dash-parts 1..5.
    const parts = groupName.split("-");
    return parts.length >= 7 ? parts.slice(1, 6).join("-") : null;
  }
  if (groupName.startsWith("tenant-")) {
    const rest = groupName.slice("tenant-".length);
    if (rest.endsWith(NOTIFY_ADMIN_SUFFIX)) return rest.slice(0, -NOTIFY_ADMIN_SUFFIX.length);
    if (rest.endsWith(NOTIFY_MEMBER_SUFFIX)) return rest.slice(0, -NOTIFY_MEMBER_SUFFIX.length);
    return rest;
  }
  return null;
}

/**
 * The status the backend answers this join with by rule — 400 for a group name it cannot parse,
 * 403 for a refusal — or null when the join may be admitted. Without a caller (auth/me not
 * resolved yet) only the name check applies; the backend decides the rest.
 */
export function refusedJoinStatus(
  groupName: string,
  caller: GroupJoinCaller | null,
  serialNumber?: string,
): 400 | 403 | null {
  if (groupName === GLOBAL_ADMINS_GROUP) {
    return caller && !caller.isGlobalAdmin && !caller.isGlobalReader ? 403 : null;
  }

  const tenantId = groupTenantId(groupName);
  if (!tenantId) return 400;
  if (!caller?.tenantId || tenantId.toLowerCase() !== caller.tenantId.toLowerCase()) return null;

  if (groupName.endsWith(NOTIFY_ADMIN_SUFFIX)) {
    return caller.isTenantAdmin || caller.isGlobalAdmin ? null : 403;
  }
  if (hasOwnTenantOrPlatformRole(caller)) return null;

  // No own-tenant role and no platform scope: the broadcast and member notification groups are
  // refused; a session group needs the device's serial number as knowledge proof.
  if (groupName.startsWith("session-")) return serialNumber ? null : 403;
  return 403;
}
