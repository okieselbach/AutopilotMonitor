import { describe, it, expect } from "vitest";
import { isSubmissionsAdminOnly } from "../MySubmissionsList";

describe("isSubmissionsAdminOnly", () => {
  it("lists a foreign tenant's submissions for a platform-scope override", () => {
    expect(isSubmissionsAdminOnly(true, { isGlobalOverride: true, isDelegatedScope: false })).toBe(false);
  });

  it("shows a delegated (MSP) reader the hint on a managed tenant — the operator list is closed to them", () => {
    expect(isSubmissionsAdminOnly(true, { isGlobalOverride: true, isDelegatedScope: true })).toBe(true);
  });

  it("shows an own-tenant Operator or Viewer the hint", () => {
    expect(isSubmissionsAdminOnly(true, { isGlobalOverride: false, isDelegatedScope: false })).toBe(true);
  });

  it("lists the own tenant's submissions for its admin, also one who is a delegated reader elsewhere", () => {
    expect(isSubmissionsAdminOnly(false, { isGlobalOverride: false, isDelegatedScope: false })).toBe(false);
    expect(isSubmissionsAdminOnly(false, { isGlobalOverride: false, isDelegatedScope: true })).toBe(false);
  });
});
