import { describe, it, expect } from "vitest";
import { resolveTenantFilterInput } from "../tenantFilterResolve";

const A = "11111111-1111-1111-1111-111111111111";
const B = "22222222-2222-2222-2222-222222222222";
const C = "33333333-3333-3333-3333-333333333333";
const tenants = [
  { tenantId: A, domainName: "contoso.example" },
  { tenantId: B, domainName: "fabrikam.example" },
  { tenantId: C, domainName: "fabrikam-labs.example" },
];

describe("resolveTenantFilterInput", () => {
  it("passes a GUID through untouched (trimmed)", () => {
    expect(resolveTenantFilterInput(`  ${A}  `, tenants)).toBe(A);
  });

  it("returns an empty string for blank input", () => {
    expect(resolveTenantFilterInput("   ", tenants)).toBe("");
  });

  it("resolves an exact domain name case-insensitively", () => {
    expect(resolveTenantFilterInput("CONTOSO.example", tenants)).toBe(A);
  });

  it("resolves the full domain even when a shorter form of it would be ambiguous", () => {
    expect(resolveTenantFilterInput("fabrikam.example", tenants)).toBe(B);
  });

  it("resolves a unique substring match on the domain", () => {
    expect(resolveTenantFilterInput("contoso", tenants)).toBe(A);
  });

  it("resolves a unique substring match on the tenant ID", () => {
    expect(resolveTenantFilterInput("2222-2222", tenants)).toBe(B);
  });

  it("leaves an ambiguous substring unchanged", () => {
    expect(resolveTenantFilterInput("fabrikam", tenants)).toBe("fabrikam");
  });

  it("leaves an unknown domain unchanged", () => {
    expect(resolveTenantFilterInput("nowhere.example", tenants)).toBe("nowhere.example");
  });

  it("leaves the input unchanged when the tenant list is empty", () => {
    expect(resolveTenantFilterInput("contoso.example", [])).toBe("contoso.example");
  });
});
