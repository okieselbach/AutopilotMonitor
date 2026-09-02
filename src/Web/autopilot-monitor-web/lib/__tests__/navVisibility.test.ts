/**
 * Roles × nav-config × route-guards matrix for the sidebar visibility logic
 * (lib/navVisibility.ts — extracted verbatim from GlobalSidebar).
 *
 * Three layers:
 *  1. deriveNavFlags — role-state derivations (regular user, dashboard link, member tiers),
 *  2. group/item/sub-item filtering against the REAL nav config (lib/globalNavConfig),
 *  3. nav ↔ route-guard consistency: every href the sidebar shows a role must be admitted
 *     by that route's layout guard — "sidebar shows it, guard bounces it" is the drift
 *     class this pins. Known mismatches are an explicit ratchet list: fixing one forces
 *     removing its entry, a new one fails the test.
 */
import { describe, expect, it } from "vitest";
import {
  DASHBOARD_ITEM,
  EXPANDABLE_NAV_GROUPS,
  NAV_GROUPS,
  REGULAR_USER_ITEMS,
} from "../globalNavConfig";
import {
  deriveNavFlags,
  filterExpandableNavGroups,
  isNavGroupVisible,
  type NavFlags,
  type NavUser,
  type NavVisibilityInput,
} from "../navVisibility";

// ── role-state fixtures ────────────────────────────────────────────────────────

function user(overrides: Partial<NavUser> = {}): NavUser {
  return {
    isTenantAdmin: false,
    isGlobalAdmin: false,
    isDelegated: false,
    role: null,
    hasMcpAccess: false,
    canManageBootstrapTokens: false,
    bootstrapTokenEnabled: false,
    unrestrictedModeEnabled: false,
    ...overrides,
  };
}

interface RoleState {
  name: string;
  input: NavVisibilityInput;
}

const ROLE_STATES: RoleState[] = [
  {
    name: "GlobalAdmin+adminModeOn",
    input: { user: user({ isGlobalAdmin: true }), hasGlobalScope: true, hasFleetScope: true, globalAdminMode: true },
  },
  {
    name: "GlobalAdmin+adminModeOff",
    input: { user: user({ isGlobalAdmin: true }), hasGlobalScope: true, hasFleetScope: true, globalAdminMode: false },
  },
  {
    name: "GlobalReader+adminModeOn",
    input: { user: user(), hasGlobalScope: true, hasFleetScope: true, globalAdminMode: true },
  },
  {
    name: "DelegatedPure",
    input: { user: user({ isDelegated: true }), hasGlobalScope: false, hasFleetScope: true, globalAdminMode: false },
  },
  {
    name: "Delegated+OwnAdminRole",
    input: { user: user({ isDelegated: true, isTenantAdmin: true, role: "Admin" }), hasGlobalScope: false, hasFleetScope: true, globalAdminMode: false },
  },
  {
    name: "Delegated+GlobalAdmin",
    input: { user: user({ isDelegated: true, isGlobalAdmin: true }), hasGlobalScope: true, hasFleetScope: true, globalAdminMode: true },
  },
  {
    name: "TenantAdmin",
    input: { user: user({ isTenantAdmin: true, role: "Admin" }), hasGlobalScope: false, hasFleetScope: false, globalAdminMode: false },
  },
  {
    name: "Operator",
    input: { user: user({ role: "Operator" }), hasGlobalScope: false, hasFleetScope: false, globalAdminMode: false },
  },
  {
    name: "Viewer",
    input: { user: user({ role: "Viewer" }), hasGlobalScope: false, hasFleetScope: false, globalAdminMode: false },
  },
  {
    name: "RegularMember",
    input: { user: user(), hasGlobalScope: false, hasFleetScope: false, globalAdminMode: false },
  },
  {
    name: "NoUser(authLoading)",
    input: { user: null, hasGlobalScope: false, hasFleetScope: false, globalAdminMode: false },
  },
];

const flagsOf = (name: string): NavFlags =>
  deriveNavFlags(ROLE_STATES.find((s) => s.name === name)!.input);

const visibleGroupIds = (flags: NavFlags): string[] =>
  [...NAV_GROUPS, ...EXPANDABLE_NAV_GROUPS]
    .filter((g) => isNavGroupVisible(g, flags))
    .map((g) => g.id);

// ── layer 1: flag derivations ─────────────────────────────────────────────────

describe("deriveNavFlags", () => {
  it.each([
    ["GlobalAdmin+adminModeOn", false, true],
    ["GlobalReader+adminModeOn", false, true],
    ["DelegatedPure", false, true],
    ["TenantAdmin", false, true],
    ["Operator", false, true],
    ["Viewer", false, true],
    ["RegularMember", true, false],
    ["NoUser(authLoading)", true, false],
  ] as const)("%s → isRegularUser=%s, showDashboard=%s", (name, regular, dashboard) => {
    const flags = flagsOf(name);
    expect(flags.isRegularUser).toBe(regular);
    expect(flags.showDashboard).toBe(dashboard);
  });

  it("all three tenant roles count as tenant members", () => {
    for (const name of ["TenantAdmin", "Operator", "Viewer"]) {
      expect(flagsOf(name).isTenantMember).toBe(true);
    }
    expect(flagsOf("RegularMember").isTenantMember).toBe(false);
  });
});

// ── layer 2: group visibility against the real nav config ─────────────────────

describe("group visibility matrix", () => {
  it("tenant members and platform scopes see the standard groups; regular users see none", () => {
    for (const name of ["TenantAdmin", "Operator", "Viewer", "GlobalReader+adminModeOn", "GlobalAdmin+adminModeOff"]) {
      const ids = visibleGroupIds(flagsOf(name));
      for (const groupId of ["monitoring", "rules", "operations", "configuration"]) {
        expect(ids, `${name} must see '${groupId}'`).toContain(groupId);
      }
    }
    expect(visibleGroupIds(flagsOf("RegularMember"))).toEqual([]);
    expect(visibleGroupIds(flagsOf("NoUser(authLoading)"))).toEqual([]);
  });

  it("the Global Admin group needs platform scope AND the admin-mode toggle", () => {
    expect(visibleGroupIds(flagsOf("GlobalAdmin+adminModeOn"))).toContain("global-admin");
    expect(visibleGroupIds(flagsOf("GlobalReader+adminModeOn"))).toContain("global-admin");
    expect(visibleGroupIds(flagsOf("GlobalAdmin+adminModeOff"))).not.toContain("global-admin");
    expect(visibleGroupIds(flagsOf("TenantAdmin"))).not.toContain("global-admin");
  });

  it("the fleet group shows for delegated admins without platform scope only", () => {
    expect(visibleGroupIds(flagsOf("DelegatedPure"))).toContain("fleet");
    expect(visibleGroupIds(flagsOf("Delegated+OwnAdminRole"))).toContain("fleet");
    expect(visibleGroupIds(flagsOf("Delegated+GlobalAdmin"))).not.toContain("fleet");
    expect(visibleGroupIds(flagsOf("TenantAdmin"))).not.toContain("fleet");
  });

  it("a delegated admin with an own-tenant role sees fleet AND tenant groups", () => {
    const ids = visibleGroupIds(flagsOf("Delegated+OwnAdminRole"));
    expect(ids).toContain("fleet");
    expect(ids).toContain("monitoring");
    expect(ids).toContain("configuration");
    // Pure delegated: no tenant groups.
    expect(visibleGroupIds(flagsOf("DelegatedPure"))).not.toContain("configuration");
  });
});

describe("item and sub-item filtering", () => {
  const itemIds = (flags: NavFlags): string[] =>
    filterExpandableNavGroups(EXPANDABLE_NAV_GROUPS, flags).flatMap((g) => g.items.map((i) => i.id));
  const subIds = (flags: NavFlags): string[] =>
    filterExpandableNavGroups(EXPANDABLE_NAV_GROUPS, flags).flatMap((g) =>
      g.items.flatMap((i) => i.items.map((s) => s.id)));

  it("globalAdminOnly items show for the real GA and never for the read-only reader", () => {
    const gaItems = itemIds(flagsOf("GlobalAdmin+adminModeOn"));
    const readerItems = itemIds(flagsOf("GlobalReader+adminModeOn"));
    for (const id of ["ga-settings", "ga-ops", "ga-software"]) {
      expect(gaItems).toContain(id);
      expect(readerItems, `reader must not see '${id}'`).not.toContain(id);
    }
    // The reader keeps the non-mutating GA items.
    for (const id of ["ga-tenants", "ga-metrics", "ga-reports", "ga-security"]) {
      expect(readerItems).toContain(id);
    }
  });

  it("cfg-reporting requires per-user MCP access", () => {
    expect(itemIds(flagsOf("TenantAdmin"))).not.toContain("cfg-reporting");
    const withMcp = deriveNavFlags({
      user: user({ isTenantAdmin: true, role: "Admin", hasMcpAccess: true }),
      hasGlobalScope: false, hasFleetScope: false, globalAdminMode: false,
    });
    expect(itemIds(withMcp)).toContain("cfg-reporting");
  });

  it("tenant-admin-only sub-sections hide from Operators and Viewers but not platform scopes", () => {
    for (const sub of ["cfg-autopilot", "cfg-access-mgmt", "cfg-offboarding", "cfg-delegations"]) {
      expect(subIds(flagsOf("TenantAdmin"))).toContain(sub);
      expect(subIds(flagsOf("GlobalReader+adminModeOn"))).toContain(sub);
      expect(subIds(flagsOf("Operator")), `Operator must not see '${sub}'`).not.toContain(sub);
      expect(subIds(flagsOf("Viewer")), `Viewer must not see '${sub}'`).not.toContain(sub);
    }
  });

  it("feature-flagged sub-items follow their tenant flags and role gates", () => {
    // Off by default:
    expect(subIds(flagsOf("TenantAdmin"))).not.toContain("cfg-bootstrap-sessions");
    expect(subIds(flagsOf("TenantAdmin"))).not.toContain("cfg-agent-unrestricted");
    // Flag on + admin:
    const flaggedAdmin = deriveNavFlags({
      user: user({ isTenantAdmin: true, role: "Admin", bootstrapTokenEnabled: true, unrestrictedModeEnabled: true }),
      hasGlobalScope: false, hasFleetScope: false, globalAdminMode: false,
    });
    expect(subIds(flaggedAdmin)).toContain("cfg-bootstrap-sessions");
    expect(subIds(flaggedAdmin)).toContain("cfg-agent-unrestricted");
    // Bootstrap manager permission substitutes for admin on bootstrap sessions only:
    const bootstrapOperator = deriveNavFlags({
      user: user({ role: "Operator", canManageBootstrapTokens: true, bootstrapTokenEnabled: true, unrestrictedModeEnabled: true }),
      hasGlobalScope: false, hasFleetScope: false, globalAdminMode: false,
    });
    expect(subIds(bootstrapOperator)).toContain("cfg-bootstrap-sessions");
    expect(subIds(bootstrapOperator)).not.toContain("cfg-agent-unrestricted");
  });
});

// ── layer 3: nav ↔ route-guard consistency ────────────────────────────────────

describe("nav ↔ route-guard consistency", () => {
  /**
   * Independent oracle of the layout guards (NOT derived from the components):
   *  - app/admin/layout.tsx → ProtectedRoute requireGlobalScope; the deeper GA-only
   *    layouts (settings/ops/backups/customs-archive/software) → requireGlobalAdmin,
   *  - app/sessions/inspector/layout.tsx → requireGlobalAdmin,
   *  - app/fleet/layout.tsx → requireFleetScope,
   *  - app/settings/layout.tsx → any own-tenant role or platform scope
   *    (hasOwnTenantOrPlatformRole; user decision 2026-08-13 — mirrors the sidebar's
   *    Configuration visibility, read-only enforced by the data layer).
   * Longest prefix wins. Routes without a declared guard have no layout gate.
   */
  const ROUTE_GUARDS: Array<{ prefix: string; admits: (input: NavVisibilityInput, flags: NavFlags) => boolean }> = [
    { prefix: "/admin/settings", admits: (_i, f) => f.isGlobalAdmin },
    { prefix: "/admin/ops", admits: (_i, f) => f.isGlobalAdmin },
    { prefix: "/admin/backups", admits: (_i, f) => f.isGlobalAdmin },
    { prefix: "/admin/customs-archive", admits: (_i, f) => f.isGlobalAdmin },
    { prefix: "/admin/software", admits: (_i, f) => f.isGlobalAdmin },
    { prefix: "/sessions/inspector", admits: (_i, f) => f.isGlobalAdmin },
    { prefix: "/admin", admits: (_i, f) => f.hasGlobalScope },
    { prefix: "/fleet", admits: (_i, f) => f.hasFleetScope },
    {
      prefix: "/settings",
      admits: (_i, f) => f.isTenantMember || f.hasGlobalScope,
    },
  ];

  /**
   * Ratchet of KNOWN sidebar-shows-it/guard-bounces-it mismatches. Empty since the
   * 2026-08-13 user decision opened /settings to Operators and the GlobalReader
   * (read-only). Any NEW mismatch fails the test; entries may only ever be removed.
   */
  const KNOWN_MISMATCHES = new Set<string>();

  const guardFor = (href: string) =>
    ROUTE_GUARDS.filter((g) => href === g.prefix || href.startsWith(g.prefix + "/"))
      .sort((a, b) => b.prefix.length - a.prefix.length)[0];

  const visibleHrefs = (input: NavVisibilityInput): string[] => {
    const flags = deriveNavFlags(input);
    const hrefs: string[] = [];
    if (flags.showDashboard) hrefs.push(DASHBOARD_ITEM.href);
    if (flags.isRegularUser) hrefs.push(...REGULAR_USER_ITEMS.map((i) => i.href));
    for (const group of NAV_GROUPS.filter((g) => isNavGroupVisible(g, flags))) {
      hrefs.push(...group.items.map((i) => i.href));
    }
    for (const group of filterExpandableNavGroups(EXPANDABLE_NAV_GROUPS, flags)) {
      hrefs.push(...group.items.flatMap((i) => i.items.map((s) => s.href)));
    }
    return hrefs;
  };

  it("every visible href is admitted by its route guard (known mismatches ratcheted)", () => {
    const mismatches = new Set<string>();
    for (const state of ROLE_STATES) {
      const flags = deriveNavFlags(state.input);
      for (const href of visibleHrefs(state.input)) {
        const guard = guardFor(href);
        if (!guard) continue;
        if (!guard.admits(state.input, flags)) {
          mismatches.add(`${state.name} × ${guard.prefix}`);
        }
      }
    }
    expect([...mismatches].sort()).toEqual([...KNOWN_MISMATCHES].sort());
  });

  it("guard oracle covers every /admin, /fleet and /settings href in the nav config", () => {
    const allHrefs = [
      DASHBOARD_ITEM.href,
      ...REGULAR_USER_ITEMS.map((i) => i.href),
      ...NAV_GROUPS.flatMap((g) => g.items.map((i) => i.href)),
      ...EXPANDABLE_NAV_GROUPS.flatMap((g) => g.items.flatMap((i) => i.items.map((s) => s.href))),
    ];
    for (const href of allHrefs) {
      if (/^\/(admin|fleet|settings)(\/|$)/.test(href) || href.startsWith("/sessions/inspector")) {
        expect(guardFor(href), `no guard oracle entry covers '${href}'`).toBeTruthy();
      }
    }
  });
});
