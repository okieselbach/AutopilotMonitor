import { describe, it, expect } from "vitest";
import type { TenantAdminRow } from "@/utils/wire-types.generated";
import {
  MEMBER_PAGE_SIZE,
  effectiveMemberFilter,
  effectiveMemberRole,
  matchesMemberFilter,
  matchesMemberSearch,
  memberCounts,
  memberPage,
  permissionChange,
  sortMembers,
  visibleMemberFilters,
} from "../memberListModel";

const APP_ID = "0f0e0d0c-1111-2222-3333-444455556666";

function row(upn: string, overrides: Partial<TenantAdminRow> = {}): TenantAdminRow {
  return {
    tenantId: "11111111-2222-3333-4444-555555555555",
    upn,
    isEnabled: true,
    addedDate: "2026-09-01T10:00:00Z",
    addedBy: "first.admin@tenant.example",
    role: "Viewer",
    canManageBootstrapTokens: false,
    ...overrides,
  };
}

describe("effectiveMemberRole", () => {
  it("reads a row without a role as Admin (pre-role rows)", () => {
    expect(effectiveMemberRole({ upn: "legacy@tenant.example" })).toBe("Admin");
    expect(effectiveMemberRole({ upn: "op@tenant.example", role: "Operator" })).toBe("Operator");
  });

  it("caps a service principal at Viewer whatever its row says", () => {
    expect(effectiveMemberRole({ upn: `app:${APP_ID}`, role: "Admin" })).toBe("Viewer");
    expect(effectiveMemberRole({ upn: `APP:${APP_ID}` })).toBe("Viewer");
  });
});

describe("memberCounts", () => {
  it("counts every role by its effective value and the disabled entries on top", () => {
    const counts = memberCounts([
      row("a@tenant.example", { role: undefined }),
      row("b@tenant.example", { role: "Admin", isEnabled: false }),
      row("c@tenant.example", { role: "Operator" }),
      row("d@tenant.example"),
      row(`app:${APP_ID}`, { role: "Admin" }),
    ]);
    expect(counts).toEqual({ all: 5, Admin: 2, Operator: 1, Viewer: 2, disabled: 1 });
  });
});

describe("chips", () => {
  it("always offers All and the three roles, Disabled only while an entry is disabled", () => {
    const enabledOnly = memberCounts([row("a@tenant.example", { role: "Admin" })]);
    expect(visibleMemberFilters(enabledOnly)).toEqual(["all", "Admin", "Operator", "Viewer"]);

    const withDisabled = memberCounts([row("a@tenant.example", { isEnabled: false })]);
    expect(visibleMemberFilters(withDisabled)).toEqual(["all", "Admin", "Operator", "Viewer", "disabled"]);
  });

  it("falls back to All when the selected chip has no entries left", () => {
    const counts = memberCounts([row("a@tenant.example", { role: "Admin" })]);
    expect(effectiveMemberFilter("Admin", counts)).toBe("Admin");
    expect(effectiveMemberFilter("Operator", counts)).toBe("all");
    expect(effectiveMemberFilter("disabled", counts)).toBe("all");
    expect(effectiveMemberFilter("all", memberCounts([]))).toBe("all");
  });

  it("filters by effective role or by the disabled state", () => {
    const disabledAdmin = row("b@tenant.example", { role: "Admin", isEnabled: false });
    expect(matchesMemberFilter(disabledAdmin, "Admin")).toBe(true);
    expect(matchesMemberFilter(disabledAdmin, "disabled")).toBe(true);
    expect(matchesMemberFilter(disabledAdmin, "Viewer")).toBe(false);
    expect(matchesMemberFilter(row(`app:${APP_ID}`, { role: "Admin" }), "Viewer")).toBe(true);
    expect(matchesMemberFilter(row("c@tenant.example"), "all")).toBe(true);
  });
});

describe("matchesMemberSearch", () => {
  it("matches the shown name ignoring case and surrounding spaces", () => {
    expect(matchesMemberSearch(row("Jane.Doe@tenant.example"), "  jane.DOE ")).toBe(true);
    expect(matchesMemberSearch(row("jane.doe@tenant.example"), "john")).toBe(false);
    expect(matchesMemberSearch(row("jane.doe@tenant.example"), "")).toBe(true);
  });

  it("finds a service principal by its label or client id, not by the key prefix", () => {
    const app = row(`app:${APP_ID}`);
    expect(matchesMemberSearch(app, "service principal")).toBe(true);
    expect(matchesMemberSearch(app, APP_ID.slice(0, 8))).toBe(true);
    expect(matchesMemberSearch(app, "app:")).toBe(false);
  });
});

describe("sortMembers", () => {
  it("lists people alphabetically before service principals and leaves the input untouched", () => {
    const input = [
      row(`app:${APP_ID}`),
      row("zoe@tenant.example"),
      row("Anna@tenant.example"),
      row("bert@tenant.example"),
    ];
    const sorted = sortMembers(input).map((m) => m.upn);
    expect(sorted).toEqual(["Anna@tenant.example", "bert@tenant.example", "zoe@tenant.example", `app:${APP_ID}`]);
    expect(input[0].upn).toBe(`app:${APP_ID}`);
  });
});

describe("permissionChange", () => {
  const operator = row("op@tenant.example", { role: "Operator", canManageBootstrapTokens: true });

  it("has nothing to save while the draft equals the stored row", () => {
    expect(permissionChange(operator, "Operator", true)).toEqual({ dirty: false, canManageBootstrapTokens: true });
    expect(permissionChange(row("legacy@tenant.example", { role: undefined }), "Admin", false).dirty).toBe(false);
  });

  it("saves a role change and keeps the stored bootstrap flag for a non-Operator role", () => {
    expect(permissionChange(operator, "Viewer", false)).toEqual({ dirty: true, canManageBootstrapTokens: true });
  });

  it("saves the bootstrap draft only for an Operator", () => {
    expect(permissionChange(operator, "Operator", false)).toEqual({ dirty: true, canManageBootstrapTokens: false });
    const viewer = row("v@tenant.example", { role: "Viewer", canManageBootstrapTokens: false });
    expect(permissionChange(viewer, "Viewer", true)).toEqual({ dirty: false, canManageBootstrapTokens: false });
    expect(permissionChange(viewer, "Operator", true)).toEqual({ dirty: true, canManageBootstrapTokens: true });
  });
});

describe("memberPage", () => {
  const rows = Array.from({ length: 2 * MEMBER_PAGE_SIZE + 3 }, (_, i) => i);

  it("slices full pages and a short last page", () => {
    expect(memberPage(rows, 0)).toMatchObject({ page: 0, pageCount: 3 });
    expect(memberPage(rows, 0).rows).toHaveLength(MEMBER_PAGE_SIZE);
    expect(memberPage(rows, 2).rows).toEqual([50, 51, 52]);
  });

  it("clamps a page past the end to the last page, so a shrunken list never shows an empty page", () => {
    const shrunk = rows.slice(0, MEMBER_PAGE_SIZE + 1);
    const shown = memberPage(shrunk, 2);
    expect(shown.page).toBe(1);
    expect(shown.rows).toEqual([MEMBER_PAGE_SIZE]);
    expect(memberPage(rows, -4).page).toBe(0);
  });

  it("keeps one empty page for an empty list", () => {
    expect(memberPage([], 3)).toEqual({ rows: [], page: 0, pageCount: 1 });
  });
});
