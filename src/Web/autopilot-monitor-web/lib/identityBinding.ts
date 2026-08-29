/**
 * The Entra identity (home tenant + object id) a cross-tenant-role UPN is bound to, as returned by
 * /api/global/identity-bindings (camelCase JSON). A grant is only usable from this identity: the backend
 * refuses tokens whose tid/oid do not match. Maintained automatically — resolved from sign-in history at
 * grant time, object id pinned on the first sign-in.
 */
export interface IdentityBinding {
  upn: string;
  tenantId: string;
  objectId: string; // "" until pinned
  boundBy: string;
  boundAt: string;
  objectIdPinnedAt: string | null;
  isObjectIdPinned: boolean;
}

/** Error code the grant endpoints return (HTTP 422) when the person's home tenant could not be resolved. */
export const HOME_TENANT_UNRESOLVED = "HomeTenantUnresolved";

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** True for a canonical GUID string (what Entra tenant and object ids look like). */
export function isGuid(value: string): boolean {
  return GUID_RE.test(value.trim());
}
