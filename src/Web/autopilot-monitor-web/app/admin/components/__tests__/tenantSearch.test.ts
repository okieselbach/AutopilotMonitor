import { describe, it, expect } from "vitest";
import {
  matchesTenantSearch,
  notificationEmailFor,
  parseTenantSearch,
  type TenantSearchFields,
} from "../tenantSearch";

const tenant: TenantSearchFields = {
  tenantId: "11111111-2222-3333-4444-555555555555",
  domainName: "contoso.onmicrosoft.com",
  contactEmail: "billing@contoso.com",
  companyName: "Contoso Ltd.",
};

const matches = (query: string, notificationEmail?: string | null, t = tenant) =>
  matchesTenantSearch(t, parseTenantSearch(query), notificationEmail);

describe("tenant search", () => {
  it("matches domain and tenant id by substring, case-insensitively", () => {
    expect(matches("CONTOSO")).toBe(true);
    expect(matches("2222-3333")).toBe(true);
    expect(matches("fabrikam")).toBe(false);
  });

  it("matches an empty query (unfiltered list)", () => {
    expect(matches("")).toBe(true);
  });

  it("finds the tenant by its notification address — the welcome-mail recipient", () => {
    expect(matches("admin@contoso.de", "admin@contoso.de")).toBe(true);
    // Partial addresses work too: a local part alone is enough to narrow the list.
    expect(matches("admin@", "admin@contoso.de")).toBe(true);
    // Without the map the address is simply not searchable — never a false hit.
    expect(matches("admin@contoso.de")).toBe(false);
  });

  it("finds the tenant by its contact address", () => {
    expect(matches("billing@contoso.com")).toBe(true);
  });

  it("does not match addresses of other tenants", () => {
    expect(matches("someone@fabrikam.com", "admin@contoso.de")).toBe(false);
  });

  it("treats a quoted query as exact equality on every field", () => {
    const exactDomain: TenantSearchFields = { ...tenant, domainName: "contoso.com" };
    // The point of the quoted mode: contoso.com is a substring of contoso.com.example,
    // so only equality can isolate it.
    expect(matches('"contoso.com"', null, exactDomain)).toBe(true);
    expect(matches('"contoso.com"', null, { ...tenant, domainName: "contoso.com.example" }))
      .toBe(false);

    // Addresses honour the same rule.
    expect(matches('"admin@contoso.de"', "admin@contoso.de")).toBe(true);
    expect(matches('"admin@"', "admin@contoso.de")).toBe(false);
  });

  it("matches the company name so support can find a tenant by what the customer calls itself", () => {
    expect(matches("ltd")).toBe(true);
    expect(matches('"contoso ltd."')).toBe(true);
    expect(matches("ltd", null, { ...tenant, companyName: null })).toBe(false);
  });

  it("ignores empty and missing address fields instead of matching everything", () => {
    const noContact: TenantSearchFields = { ...tenant, contactEmail: null };
    expect(matches('""', null, noContact)).toBe(false);
    expect(matches('""', "", noContact)).toBe(false);
  });

  it("looks addresses up case-insensitively on the tenant id", () => {
    const emails = { "aaaa-bbbb": "admin@contoso.de" };
    expect(notificationEmailFor(emails, "AAAA-BBBB")).toBe("admin@contoso.de");
    expect(notificationEmailFor(emails, "cccc-dddd")).toBeUndefined();
  });
});
