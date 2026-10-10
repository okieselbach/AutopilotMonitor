import { describe, it, expect } from "vitest";
import { boundTenantToDelegatedScope } from "../delegatedScope";

const MANAGED = "11111111-1111-1111-1111-111111111111";
const UNMANAGED = "99999999-9999-9999-9999-999999999999";

describe("boundTenantToDelegatedScope", () => {
  it("passes a managed tenant through for a delegated caller (case-insensitive)", () => {
    expect(boundTenantToDelegatedScope(MANAGED, true, [MANAGED])).toBe(MANAGED);
    expect(boundTenantToDelegatedScope(MANAGED.toUpperCase(), true, [MANAGED])).toBe(MANAGED.toUpperCase());
  });

  it("drops an unmanaged tenant to undefined for a delegated caller (the deep-link guard)", () => {
    expect(boundTenantToDelegatedScope(UNMANAGED, true, [MANAGED])).toBeUndefined();
    // Empty managed set ⇒ nothing is in scope.
    expect(boundTenantToDelegatedScope(UNMANAGED, true, [])).toBeUndefined();
    expect(boundTenantToDelegatedScope(UNMANAGED, true, undefined)).toBeUndefined();
  });

  it("never bounds a non-delegated caller (GA/Reader/member are unbounded)", () => {
    expect(boundTenantToDelegatedScope(UNMANAGED, false, undefined)).toBe(UNMANAGED);
    expect(boundTenantToDelegatedScope(UNMANAGED, false, [MANAGED])).toBe(UNMANAGED);
  });

  it("passes undefined/empty through unchanged (bounded aggregate, not a drill)", () => {
    expect(boundTenantToDelegatedScope(undefined, true, [MANAGED])).toBeUndefined();
    expect(boundTenantToDelegatedScope("", true, [MANAGED])).toBe("");
  });
});
