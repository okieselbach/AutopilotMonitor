import { describe, expect, it } from "vitest";
import {
  ANNOTATION_MAX_NOTE_LENGTH,
  buildPutBody,
  hasContent,
  validateNote,
  visibleLanes,
  writableLaneSet,
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

describe("writableLaneSet", () => {
  // The write MATRIX itself lives server-side (UpsertSessionAnnotationFunction.
  // IsLaneWritableByCaller, served as `writableLanes` on the GET) — the web only
  // normalizes the list, so these tests pin the normalization, not the matrix.
  it("normalizes the server list case-insensitively", () => {
    const set = writableLaneSet(["Operator", "GLOBALADMIN"]);
    expect(set.has("operator")).toBe(true);
    expect(set.has("globaladmin")).toBe(true);
    expect(set.has("tenantadmin")).toBe(false);
  });

  it("fails closed when the field is absent (older backend) or empty", () => {
    expect(writableLaneSet(undefined).size).toBe(0);
    expect(writableLaneSet(null).size).toBe(0);
    expect(writableLaneSet([]).size).toBe(0);
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
