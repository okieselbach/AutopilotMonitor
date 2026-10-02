import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { TenantAdminRow } from "@/utils/wire-types.generated";
import { MemberList } from "../MemberList";
import { MEMBER_PAGE_SIZE } from "../memberListModel";

const noop = () => {};

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

function render(members: TenantAdminRow[], currentUserUpn?: string) {
  return renderToStaticMarkup(
    createElement(MemberList, {
      members,
      loading: false,
      currentUserUpn,
      removingUpn: null,
      updatingUpn: null,
      onRemove: noop,
      onToggleEnabled: noop,
      onUpdatePermissions: noop,
    }),
  );
}

describe("MemberList", () => {
  it("renders closed one-line rows: badges only, the actions wait behind a click", () => {
    const html = render([
      row("admin@tenant.example", { role: "Admin" }),
      row("op@tenant.example", { role: "Operator", canManageBootstrapTokens: true }),
      row("off@tenant.example", { isEnabled: false }),
    ]);
    expect(html.match(/aria-expanded="false"/g)?.length).toBe(3);
    expect(html).toContain("Bootstrap tokens");
    expect(html).toContain(">Disabled</span>");
    expect(html).not.toContain("<select");
    expect(html).not.toContain(">Remove</button>");
    expect(html).not.toContain(">Disable</button>");
  });

  it("shows the role chips with counts, All pressed, and Disabled only when an entry is disabled", () => {
    const html = render([
      row("a@tenant.example", { role: "Admin" }),
      row("b@tenant.example", { role: "Viewer" }),
      row("c@tenant.example", { role: "Viewer" }),
    ]);
    const chips = html.split('<div role="group" aria-label="Filter members by role"')[1].split("</div>")[0];
    expect(chips).toContain('aria-pressed="true"');
    expect(chips.match(/<button /g)?.length).toBe(4);
    expect(chips).toMatch(/All<span[^>]*>3<\/span>/);
    expect(chips).toMatch(/Viewer<span[^>]*>2<\/span>/);
    // No Operator yet: the chip stays in place but cannot be picked.
    expect(chips).toMatch(/disabled=""[^>]*>Operator<span[^>]*>0<\/span>/);
    expect(chips).not.toContain("Disabled");
  });

  it("marks the signed-in user's row", () => {
    const html = render([row("me@tenant.example", { role: "Admin" }), row("other@tenant.example")], "ME@tenant.example");
    const ownRow = html.split("<li").find((chunk) => chunk.includes("me@tenant.example"));
    expect(ownRow).toContain(">You</span>");
    const otherRow = html.split("<li").find((chunk) => chunk.includes("other@tenant.example"));
    expect(otherRow).not.toContain(">You</span>");
  });

  it(`pages at ${MEMBER_PAGE_SIZE} rows`, () => {
    const members = Array.from({ length: MEMBER_PAGE_SIZE + 5 }, (_, i) => row(`user${String(i).padStart(2, "0")}@tenant.example`));
    const html = render(members);
    expect(html.match(/<li/g)?.length).toBe(MEMBER_PAGE_SIZE);
    expect(html).toContain(`Page 1 of 2 (${MEMBER_PAGE_SIZE + 5} members)`);
  });

  it("renders no pager for one page and an empty state without members", () => {
    expect(render([row("a@tenant.example")])).not.toContain("Next");
    expect(render([])).toContain("No members found");
  });
});
