import type { TenantInfo } from "@/hooks/useTenantList";

/**
 * Home-tenant support for delegated ("MSP") admins — the web analog of the MCP fix (88a432dc).
 *
 * A delegated admin who is ALSO a member/operator of their OWN home tenant must keep seeing it in the
 * tenant switcher and be able to read it. That access is member-based (JWT-bound `/api/*` endpoints),
 * NOT a delegated grant: the home tenant is never in `delegatedTenantIds`, and every `/api/global/*`
 * endpoint bounds a delegated caller to the managed set (a home-tenant request there returns an empty
 * page). So the home tenant is ADDED to the visible list here, and the scope hooks route it to the
 * tenant-scoped member path. The backend stays authoritative — a delegated caller who is NOT actually
 * a member of their home tenant is simply denied on the member path, so surfacing the id never leaks.
 */

/** Case-insensitive "is this candidate the caller's home tenant" check. Falsy inputs never match. */
export function isHomeTenantTarget(candidateTenantId: string | undefined, homeTenantId: string | undefined): boolean {
  if (!candidateTenantId || !homeTenantId) return false;
  return candidateTenantId.toLowerCase() === homeTenantId.toLowerCase();
}

/**
 * Display label for the home tenant when config/all (backend-bounded to the managed set for a delegated
 * caller) did not return its config row: the UPN suffix. The backend derives a tenant's DomainName from
 * a member's UPN suffix at onboarding anyway, so this matches what the real row would say.
 */
export function upnDomain(upn: string | undefined): string {
  const at = (upn ?? "").indexOf("@");
  return at >= 0 ? (upn as string).slice(at + 1) : "";
}

/**
 * The tenant list a delegated ("MSP") caller may address: the managed subset of `allTenants`, plus the
 * caller's home tenant when they have a member role there (`hasHomeRole`). The home entry is synthesized
 * (with the UPN-derived domain) when the backend-bounded config/all did not include it; if the home tenant
 * happens to also be managed, the existing entry is kept and only flagged `isHome`. Result stays sorted by
 * domain name. Without a home role the old strict managed-set behavior holds (no phantom home access).
 *
 * An EMPTY `allTenants` returns empty — no home synthesis. The list is empty exactly while the config/all
 * fetch is still in flight (a delegated caller always has ≥1 managed tenant), and synthesizing home into
 * that window would race the scope hooks' render-time seeding: `tenants[0]` would be the home entry, so a
 * persisted MANAGED selection (not yet in the list) would be discarded and home would win as the default.
 */
export function delegatedScopedTenantList(
  allTenants: TenantInfo[],
  delegatedTenantIds: string[] | undefined,
  homeTenantId: string | undefined,
  homeDomainName: string,
  hasHomeRole: boolean,
): TenantInfo[] {
  if (allTenants.length === 0) return [];
  const allow = new Set((delegatedTenantIds ?? []).map((t) => t.toLowerCase()));
  const scoped = allTenants
    .filter((t) => allow.has(t.tenantId.toLowerCase()))
    .map((t) => (isHomeTenantTarget(t.tenantId, homeTenantId) ? { ...t, isHome: true } : t));

  if (hasHomeRole && homeTenantId && !scoped.some((t) => t.isHome)) {
    scoped.push({ tenantId: homeTenantId, domainName: homeDomainName, isHome: true });
    scoped.sort((a, b) => (a.domainName || a.tenantId).localeCompare(b.domainName || b.tenantId));
  }
  return scoped;
}
