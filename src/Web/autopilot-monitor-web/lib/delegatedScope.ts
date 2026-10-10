/**
 * Defense-in-depth for the delegated ("MSP") cross-tenant views.
 *
 * The backend already bounds every delegated request to the caller's managed tenant set, so this is NOT
 * the security boundary — it just keeps the client honest: a delegated reader must never even SEND an
 * out-of-scope tenantId to a `/global/*` endpoint. The tenant-switcher dropdowns are already intersected
 * with the managed set, but a hand-crafted `?tenant=`/`?tenantId=` deep link or a free-typed GUID can still
 * carry an unmanaged (or another customer's) tenant. This collapses such a value to `undefined` so the
 * request degrades to the bounded aggregate (or is rejected server-side) instead of asking for one tenant
 * the caller does not manage.
 *
 * A non-delegated caller (Global Admin / Global Reader, or a normal tenant member) is unbounded here and
 * passes through unchanged.
 *
 * @param tenantId            the candidate tenant id to send (already GUID-validated / may be undefined)
 * @param isDelegatedScope    true only for a delegated caller WITHOUT platform scope (GA/Reader are unbounded)
 * @param delegatedTenantIds  the caller's managed tenant ids (any casing)
 * @returns the tenantId when allowed, otherwise undefined
 */
export function boundTenantToDelegatedScope(
  tenantId: string | undefined,
  isDelegatedScope: boolean,
  delegatedTenantIds: string[] | undefined,
): string | undefined {
  if (!tenantId || !isDelegatedScope) return tenantId;
  const allow = new Set((delegatedTenantIds ?? []).map((t) => t.toLowerCase()));
  return allow.has(tenantId.toLowerCase()) ? tenantId : undefined;
}
