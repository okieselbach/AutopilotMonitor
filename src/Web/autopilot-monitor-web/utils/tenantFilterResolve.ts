import { isGuid } from "@/utils/inputValidation";

export interface TenantFilterCandidate {
  tenantId: string;
  domainName: string;
}

/**
 * Resolves what a cross-tenant admin typed into the tenant filter box to a tenant ID.
 *
 * The box advertises "Tenant ID or domain name", but only a picked suggestion ever wrote
 * the GUID into it — a domain typed out and submitted with Enter/Filter stayed a raw
 * string, which the fetch path dropped (not a GUID) while the client-side scope compared
 * it against session tenant IDs → zero rows and a misleading "no sessions for this tenant".
 *
 * Resolution order: GUID passes through; an exact (case-insensitive) domain match wins;
 * a single substring match on domain or ID is unambiguous enough to take; anything else
 * is returned unchanged so the caller can tell the user nothing matched.
 */
export function resolveTenantFilterInput(input: string, tenantList: readonly TenantFilterCandidate[]): string {
  const trimmed = input.trim();
  if (!trimmed || isGuid(trimmed)) return trimmed;

  const q = trimmed.toLowerCase();
  const exact = tenantList.find((t) => t.domainName.toLowerCase() === q);
  if (exact) return exact.tenantId;

  const partial = tenantList.filter(
    (t) => t.domainName.toLowerCase().includes(q) || t.tenantId.toLowerCase().includes(q),
  );
  return partial.length === 1 ? partial[0].tenantId : trimmed;
}
