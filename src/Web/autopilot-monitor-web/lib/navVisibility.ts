import type { ExpandableNavGroup, NavGroup } from "./globalNavConfig";

/**
 * Pure sidebar-visibility logic, extracted from GlobalSidebar so the full
 * roles × nav-config matrix is testable without a DOM (house pattern — see
 * aggregatedAdminScopeSeed / decideHostBounce). GlobalSidebar consumes these
 * verbatim; behavior changes belong HERE so the matrix test sees them.
 */

/** Structural subset of AuthContext's UserInfo the nav logic reads. */
export interface NavUser {
  isTenantAdmin?: boolean;
  isGlobalAdmin?: boolean;
  isDelegated?: boolean;
  role?: string | null;
  hasMcpAccess?: boolean;
  canManageBootstrapTokens?: boolean;
  bootstrapTokenEnabled?: boolean;
  unrestrictedModeEnabled?: boolean;
}

export interface NavVisibilityInput {
  user: NavUser | null | undefined;
  /** AuthContext derivation: isGlobalAdmin || isGlobalReader. */
  hasGlobalScope: boolean;
  /** AuthContext derivation: isGlobalAdmin || isGlobalReader || isDelegated. */
  hasFleetScope: boolean;
  /** The Global View toggle (localStorage) — gates the purple Global Admin section. */
  globalAdminMode: boolean;
}

export interface NavFlags {
  isTenantMember: boolean;
  isGlobalAdmin: boolean;
  isDelegated: boolean;
  hasGlobalScope: boolean;
  hasFleetScope: boolean;
  globalAdminMode: boolean;
  hasMcpAccess: boolean;
  isAdminLike: boolean;
  canManageBootstrapTokens: boolean;
  bootstrapTokenEnabled: boolean;
  unrestrictedModeEnabled: boolean;
  /** Minimal nav (Progress Portal only): no tenant role and no fleet/platform scope. */
  isRegularUser: boolean;
  /** The cross-tenant session browser link. */
  showDashboard: boolean;
}

export function deriveNavFlags({ user, hasGlobalScope, hasFleetScope, globalAdminMode }: NavVisibilityInput): NavFlags {
  const isTenantAdmin = user?.isTenantAdmin ?? false;
  const isOperator = user?.role === "Operator";
  const isViewer = user?.role === "Viewer";
  // Any resolved tenant role — Admin, Operator, or read-only Viewer. The Viewer sees the same
  // monitoring/rules/operations/configuration nav; read-only is enforced inside the pages.
  const isTenantMember = isTenantAdmin || isOperator || isViewer;
  const isGlobalAdmin = user?.isGlobalAdmin ?? false;
  const isDelegated = user?.isDelegated ?? false;

  return {
    isTenantMember,
    isGlobalAdmin,
    isDelegated,
    hasGlobalScope,
    hasFleetScope,
    globalAdminMode,
    hasMcpAccess: user?.hasMcpAccess ?? false,
    isAdminLike: isTenantAdmin || isGlobalAdmin,
    canManageBootstrapTokens: user?.canManageBootstrapTokens ?? false,
    bootstrapTokenEnabled: user?.bootstrapTokenEnabled ?? false,
    unrestrictedModeEnabled: user?.unrestrictedModeEnabled ?? false,
    // Regular users see minimal nav. A read-only Global Reader has platform scope, and a
    // delegated MSP admin has fleet scope → both get the group-filtered nav instead.
    isRegularUser: !isTenantMember && !hasFleetScope,
    // Own-tenant/platform users see their own/all sessions; a delegated ("MSP") admin sees a
    // bounded aggregate across their managed tenants — so they get the link too.
    showDashboard: isTenantMember || hasGlobalScope || isDelegated,
  };
}

export function isNavGroupVisible(group: Pick<NavGroup | ExpandableNavGroup, "visibility">, flags: NavFlags): boolean {
  switch (group.visibility) {
    case "all": return true;
    // A GlobalReader is "GA minus writes" — it must see the SAME sidebar as a real GA. The standard
    // monitoring/rules/operations/configuration groups are normally tenant-member scope; open them to any
    // platform scope so a pure GlobalReader (no own-tenant role) gets Fleet Health, Usage Metrics, SLA,
    // etc. Read-only is enforced inside the pages (useCanMutatePlatform + backend), not by hiding nav.
    case "tenantMember": return flags.isTenantMember || flags.hasGlobalScope;
    // The (purple) Global Admin section is gated on the Global View toggle — IDENTICAL to a real GA:
    // toggle off → hidden, toggle on → shown. Item-level globalAdminOnly entries (Settings/Ops/Software)
    // still drop out for a read-only reader via the item filter below.
    case "globalAdmin": return flags.hasGlobalScope && flags.globalAdminMode;
    // Fleet (MSP) nav: shown to a delegated admin who does NOT have full platform scope. A GA/Reader
    // gets the standard monitoring groups above instead, so it stays hidden for them (no duplication).
    case "fleet": return flags.isDelegated && !flags.hasGlobalScope;
    default: return false;
  }
}

/**
 * Group → item → sub-item filtering for the expandable groups (Configuration, Global Admin):
 * feature gates (MCP reporting, bootstrap sessions, unrestricted mode) and role gates
 * (globalAdminOnly items, tenant-admin-only sub-sections). Items whose sub-items all filter
 * away are dropped entirely.
 */
export function filterExpandableNavGroups(
  groups: readonly ExpandableNavGroup[],
  flags: NavFlags,
): ExpandableNavGroup[] {
  return groups
    .filter((group) => isNavGroupVisible(group, flags))
    .map((group) => {
      const filteredItems = group.items
        .filter((item) => {
          // MCP reporting requires per-user MCP access.
          if (item.id === "cfg-reporting") return flags.hasMcpAccess;
          // Platform-settings/mutation sub-sections are real-GA-only: hide from a read-only
          // Global Reader (who reaches the group via hasGlobalScope visibility).
          if ("visibility" in item && item.visibility === "globalAdminOnly") return flags.isGlobalAdmin;
          return true;
        })
        .map((item) => {
          // Sub-item gating for feature-flagged entries (tenant feature flags)
          const filteredSubs = item.items.filter((sub) => {
            if (sub.id === "cfg-bootstrap-sessions") {
              return flags.bootstrapTokenEnabled && (flags.isAdminLike || flags.canManageBootstrapTokens);
            }
            if (sub.id === "cfg-agent-unrestricted") {
              return flags.isAdminLike && flags.unrestrictedModeEnabled;
            }
            // Tenant-admin-only sub-sections: Operators and Viewers (read-only settings
            // viewers) don't see them — matches the in-page "tenant administrators only" gates. A platform
            // scope (GA / read-only GlobalReader) keeps the full GA-identical sidebar.
            if (sub.id === "cfg-autopilot" || sub.id === "cfg-access-mgmt" || sub.id === "cfg-offboarding") {
              return flags.isAdminLike || flags.hasGlobalScope;
            }
            return true;
          });
          return { ...item, items: filteredSubs };
        })
        // Drop items whose sub-items have all been filtered out
        .filter((item) => item.items.length > 0);
      return { ...group, items: filteredItems };
    });
}
