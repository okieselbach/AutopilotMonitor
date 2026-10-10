import { describe, it, expect } from "vitest";
import {
  CROSS_TENANT_FOLLOWUP_PAGE_SIZE,
  DEFAULT_PAGE_SIZE,
  MAX_PAGE_SIZE,
  resolveSessionsFetchPageSize,
} from "../sessionsPageSize";

describe("resolveSessionsFetchPageSize", () => {
  it("keeps the first paint small in every mode", () => {
    expect(resolveSessionsFetchPageSize(DEFAULT_PAGE_SIZE, { continuation: false, crossTenant: true })).toBe(DEFAULT_PAGE_SIZE);
    expect(resolveSessionsFetchPageSize(DEFAULT_PAGE_SIZE, { continuation: false, crossTenant: false })).toBe(DEFAULT_PAGE_SIZE);
  });

  it("batches cross-tenant follow-up pages — every such page costs one backend query per enrolled tenant", () => {
    expect(resolveSessionsFetchPageSize(DEFAULT_PAGE_SIZE, { continuation: true, crossTenant: true })).toBe(CROSS_TENANT_FOLLOWUP_PAGE_SIZE);
  });

  it("leaves the own-tenant cadence alone (one page there is one query)", () => {
    expect(resolveSessionsFetchPageSize(DEFAULT_PAGE_SIZE, { continuation: true, crossTenant: false })).toBe(DEFAULT_PAGE_SIZE);
  });

  it("never shrinks a larger per-page choice and stays within the backend cap", () => {
    expect(resolveSessionsFetchPageSize(250, { continuation: true, crossTenant: true })).toBe(250);
    expect(resolveSessionsFetchPageSize(MAX_PAGE_SIZE, { continuation: true, crossTenant: true })).toBe(MAX_PAGE_SIZE);
    expect(CROSS_TENANT_FOLLOWUP_PAGE_SIZE).toBeLessThanOrEqual(MAX_PAGE_SIZE);
  });
});
