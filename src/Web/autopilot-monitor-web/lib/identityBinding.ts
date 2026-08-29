/**
 * The Entra identity (home tenant + object id) a cross-tenant-role UPN is bound to, as returned by
 * /api/global/delegated-admins, /api/global/tenant-groups and /api/global/identity-bindings (camelCase JSON).
 * A grant is only usable from this identity: the backend refuses tokens whose tid/oid do not match.
 */
export interface IdentityBinding {
  upn: string;
  tenantId: string;
  objectId: string; // "" until pinned (grant without objectId ⇒ pinned on the person's first sign-in)
  boundBy: string;
  boundAt: string;
  objectIdPinnedAt: string | null;
  isObjectIdPinned: boolean;
}

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** True for a canonical GUID string (what Entra tenant and object ids look like). */
export function isGuid(value: string): boolean {
  return GUID_RE.test(value.trim());
}

/** Indexes a bindings list by lowercase UPN. */
export function bindingsByUpn(bindings: IdentityBinding[] | undefined): Map<string, IdentityBinding> {
  return new Map((bindings ?? []).map((b) => [b.upn.toLowerCase(), b]));
}

/** Short human label for a binding's state, for the per-UPN context pill. */
export function bindingLabel(b: IdentityBinding | undefined, domainOf: (tenantId: string) => string): string {
  if (!b) return "No identity binding — grant is inert";
  const home = domainOf(b.tenantId) || `tenant …${b.tenantId.slice(-4)}`;
  return b.isObjectIdPinned ? `${home} · object id pinned` : `${home} · object id pinned on first sign-in`;
}
