import { describe, expect, it } from "vitest";
import {
  ANNOTATION_MAX_NOTE_LENGTH,
  buildPutBody,
  canWriteLane,
  hasContent,
  validateNote,
  visibleLanes,
  type AnnotationUser,
} from "../sessionAnnotationLogic";

const ga: AnnotationUser = { isGlobalAdmin: true, isTenantAdmin: false, role: null };
const globalReader: AnnotationUser = { isGlobalReader: true, role: null };
const tenantAdmin: AnnotationUser = { isTenantAdmin: true, role: "Admin" };
const operator: AnnotationUser = { role: "Operator" };
const viewer: AnnotationUser = { role: "Viewer" };

describe("visibleLanes", () => {
  it("shows all three lanes for global scope (GA and Global Reader)", () => {
    expect(visibleLanes(ga)).toEqual(["operator", "tenantadmin", "globaladmin"]);
    expect(visibleLanes(globalReader)).toEqual(["operator", "tenantadmin", "globaladmin"]);
  });

  it("hides the globaladmin lane from tenant members", () => {
    for (const user of [tenantAdmin, operator, viewer]) {
      expect(visibleLanes(user)).toEqual(["operator", "tenantadmin"]);
    }
  });

  it("treats a missing user as tenant scope", () => {
    expect(visibleLanes(null)).toEqual(["operator", "tenantadmin"]);
    expect(visibleLanes(undefined)).toEqual(["operator", "tenantadmin"]);
  });
});

describe("canWriteLane (own tenant)", () => {
  it("operator lane: Operator and Tenant Admin may write, Viewer may not", () => {
    expect(canWriteLane("operator", operator, false)).toBe(true);
    expect(canWriteLane("operator", tenantAdmin, false)).toBe(true);
    expect(canWriteLane("operator", viewer, false)).toBe(false);
  });

  it("tenantadmin lane: Tenant Admin only among tenant roles", () => {
    expect(canWriteLane("tenantadmin", tenantAdmin, false)).toBe(true);
    expect(canWriteLane("tenantadmin", operator, false)).toBe(false);
    expect(canWriteLane("tenantadmin", viewer, false)).toBe(false);
  });

  it("globaladmin lane: GA only — Global Reader is read-only", () => {
    expect(canWriteLane("globaladmin", ga, false)).toBe(true);
    expect(canWriteLane("globaladmin", globalReader, false)).toBe(false);
    expect(canWriteLane("globaladmin", tenantAdmin, false)).toBe(false);
  });

  it("no user writes nothing", () => {
    expect(canWriteLane("operator", null, false)).toBe(false);
    expect(canWriteLane("globaladmin", undefined, false)).toBe(false);
  });
});

describe("canWriteLane (cross-tenant view)", () => {
  it("tenant-role lanes are never writable cross-tenant, even for a GA who is admin at home", () => {
    const gaAndHomeAdmin: AnnotationUser = { isGlobalAdmin: true, isTenantAdmin: true, role: "Admin" };
    expect(canWriteLane("operator", gaAndHomeAdmin, true)).toBe(false);
    expect(canWriteLane("tenantadmin", gaAndHomeAdmin, true)).toBe(false);
  });

  it("globaladmin lane stays writable for a GA cross-tenant (the labeling flow)", () => {
    expect(canWriteLane("globaladmin", ga, true)).toBe(true);
    expect(canWriteLane("globaladmin", globalReader, true)).toBe(false);
  });
});

describe("validateNote", () => {
  it("accepts notes up to the cap", () => {
    expect(validateNote("x".repeat(ANNOTATION_MAX_NOTE_LENGTH))).toBeNull();
    expect(validateNote("")).toBeNull();
  });

  it("rejects notes over the cap", () => {
    expect(validateNote("x".repeat(ANNOTATION_MAX_NOTE_LENGTH + 1))).toMatch(/at most 4096/);
  });
});

describe("buildPutBody", () => {
  it("nulls empty strings and detects the clear case", () => {
    // undefined, null and "" must behave alike (WhenWritingNull convention).
    for (const verdict of [undefined, null, ""]) {
      const { body, isClear } = buildPutBody(verdict, "   ");
      expect(body).toEqual({ verdict: null, note: null });
      expect(isClear).toBe(true);
    }
  });

  it("keeps verdict-only and note-only payloads", () => {
    expect(buildPutBody("analysis_wrong", "")).toEqual({
      body: { verdict: "analysis_wrong", note: null },
      isClear: false,
    });
    expect(buildPutBody(null, " real cause was networking ")).toEqual({
      body: { verdict: null, note: "real cause was networking" },
      isClear: false,
    });
  });
});

describe("hasContent", () => {
  it("treats absent, null and empty alike", () => {
    expect(hasContent(null)).toBe(false);
    expect(hasContent(undefined)).toBe(false);
    expect(hasContent({ lane: "operator" })).toBe(false);
    expect(hasContent({ lane: "operator", verdict: null, note: null })).toBe(false);
    expect(hasContent({ lane: "operator", verdict: "inconclusive" })).toBe(true);
    expect(hasContent({ lane: "operator", note: "n" })).toBe(true);
  });
});
