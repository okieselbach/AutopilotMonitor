/**
 * Search matching for the Tenant Management list.
 *
 * Four fields are searchable: tenant id, domain, the tenant-maintained contact address and
 * the notification address the welcome mail goes to. The two addresses exist for one job —
 * a delivered mail shows the operator only the recipient, so an address has to lead back to
 * its tenant. No other address is matched (admin UPNs never receive platform mail and would
 * only widen the PII surface of an operator search).
 *
 * A quoted query ("microsoft.com") matches by exact, case-insensitive equality instead of
 * substring — the only way to find a tenant whose domain is a substring of every other one.
 */

export interface TenantSearchFields {
  tenantId: string;
  domainName: string;
  contactEmail?: string | null;
  companyName?: string | null;
}

/** Parsed once per keystroke, then applied to every tenant. */
export interface TenantSearchTerm {
  /** Set when the query was quoted — equality instead of substring. */
  exact: string | null;
  /** Lowercased raw query; empty means "match everything". */
  substring: string;
}

export function parseTenantSearch(query: string): TenantSearchTerm {
  const quoted = query.trim().match(/^"(.*)"$/);
  return {
    exact: quoted ? quoted[1].toLowerCase() : null,
    substring: query.toLowerCase(),
  };
}

/**
 * @param notificationEmail the address from the cross-tenant map — the tenant object itself
 *   does not carry it (it lives in a separate table, loaded alongside the list).
 */
export function matchesTenantSearch(
  tenant: TenantSearchFields,
  term: TenantSearchTerm,
  notificationEmail?: string | null,
): boolean {
  const haystack = [tenant.tenantId, tenant.domainName, tenant.contactEmail, tenant.companyName, notificationEmail]
    .filter((v): v is string => typeof v === "string" && v.length > 0)
    .map(v => v.toLowerCase());

  return term.exact !== null
    ? haystack.some(v => v === term.exact)
    : haystack.some(v => v.includes(term.substring));
}

/**
 * Address lookup key. The address rows are written from the JWT tid (lowercase) while a
 * tenant config's PartitionKey casing is not guaranteed, so both sides join lowercased.
 */
export function notificationEmailFor(
  emails: Record<string, string>,
  tenantId: string,
): string | undefined {
  return emails[tenantId.toLowerCase()];
}
