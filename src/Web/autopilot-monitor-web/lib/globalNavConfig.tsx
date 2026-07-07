import {
  ChartBarIcon,
  GearIcon,
  DocumentTextIcon,
  ShieldCheckIcon,
  BuildingOfficeIcon,
  NoSymbolIcon,
  KeyIcon,
  SparklesIcon,
  ArrowDownTrayIcon,
  ArrowPathIcon,
  FolderIcon,
  WrenchScrewdriverIcon,
} from "./sidebarIcons";

// --- Icons defined inline (not in sidebarIcons) ---

function HomeIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M2.25 12l8.954-8.955c.44-.439 1.152-.439 1.591 0L21.75 12M4.5 9.75v10.125c0 .621.504 1.125 1.125 1.125H9.75v-4.875c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125V21h4.125c.621 0 1.125-.504 1.125-1.125V9.75M8.25 21h8.25" />
    </svg>
  );
}

function TrendingUpIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M2.25 18L9 11.25l4.306 4.307a11.95 11.95 0 015.814-5.519l2.74-1.22m0 0l-5.94-2.28m5.94 2.28l-2.28 5.941" />
    </svg>
  );
}

function GlobeAltIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M12 21a9.004 9.004 0 008.716-6.747M12 21a9.004 9.004 0 01-8.716-6.747M12 21c2.485 0 4.5-4.03 4.5-9S14.485 3 12 3m0 18c-2.485 0-4.5-4.03-4.5-9S9.515 3 12 3m0 0a8.997 8.997 0 017.843 4.582M12 3a8.997 8.997 0 00-7.843 4.582m15.686 0A11.953 11.953 0 0112 10.5c-2.998 0-5.74-1.1-7.843-2.918m15.686 0A8.959 8.959 0 0121 12c0 .778-.099 1.533-.284 2.253m0 0A17.919 17.919 0 0112 16.5c-3.162 0-6.133-.815-8.716-2.247m0 0A9.015 9.015 0 013 12c0-1.605.42-3.113 1.157-4.418" />
    </svg>
  );
}

function MonitorIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M9 17.25v1.007a3 3 0 01-.879 2.122L7.5 21h9l-.621-.621A3 3 0 0115 18.257V17.25m6-12V15a2.25 2.25 0 01-2.25 2.25H5.25A2.25 2.25 0 013 15V5.25m18 0A2.25 2.25 0 0018.75 3H5.25A2.25 2.25 0 003 5.25m18 0V12a2.25 2.25 0 01-2.25 2.25H5.25A2.25 2.25 0 013 12V5.25" />
    </svg>
  );
}

function ClipboardDocumentIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M9 12h3.75M9 15h3.75M9 18h3.75m3 .75H18a2.25 2.25 0 002.25-2.25V6.108c0-1.135-.845-2.098-1.976-2.192a48.424 48.424 0 00-1.123-.08m-5.801 0c-.065.21-.1.433-.1.664 0 .414.336.75.75.75h4.5a.75.75 0 00.75-.75 2.25 2.25 0 00-.1-.664m-5.8 0A2.251 2.251 0 0113.5 2.25H15c1.012 0 1.867.668 2.15 1.586m-5.8 0c-.376.023-.75.05-1.124.08C9.095 4.01 8.25 4.973 8.25 6.108V8.25m0 0H4.875c-.621 0-1.125.504-1.125 1.125v11.25c0 .621.504 1.125 1.125 1.125h9.75c.621 0 1.125-.504 1.125-1.125V9.375c0-.621-.504-1.125-1.125-1.125H8.25" />
    </svg>
  );
}

function HeartIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M21 8.25c0-2.485-2.099-4.5-4.688-4.5-1.935 0-3.597 1.126-4.312 2.733-.715-1.607-2.377-2.733-4.313-2.733C5.1 3.75 3 5.765 3 8.25c0 7.22 9 12 9 12s9-4.78 9-12z" />
    </svg>
  );
}

function CubeIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M21 7.5l-9-5.25L3 7.5m18 0l-9 5.25m9-5.25v9l-9 5.25M3 7.5l9 5.25M3 7.5v9l9 5.25m0-9v9" />
    </svg>
  );
}

function WrenchIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M11.42 15.17L17.25 21A2.652 2.652 0 0021 17.25l-5.877-5.877M11.42 15.17l2.496-3.03c.317-.384.74-.626 1.208-.766M11.42 15.17l-4.655 5.653a2.548 2.548 0 11-3.586-3.586l6.837-5.63m5.108-.233c.55-.164 1.163-.188 1.743-.14a4.5 4.5 0 004.486-6.336l-3.276 3.277a3.004 3.004 0 01-2.25-2.25l3.276-3.276a4.5 4.5 0 00-6.336 4.486c.091 1.076-.071 2.264-.904 2.95l-.102.085m-1.745 1.437L5.909 7.5H4.5L2.25 3.75l1.5-1.5L7.5 4.5v1.409l4.26 4.26m-1.745 1.437l1.745-1.437" />
    </svg>
  );
}

// --- Types ---

export interface NavItem {
  id: string;
  label: string;
  href: string;
  icon: React.ReactNode;
}

/** A sub-item inside an expandable group (GitHub-style, no icon) */
export interface ExpandableSubItem {
  id: string;
  label: string;
  href: string;
}

/** An expandable group with icon + chevron, containing sub-items */
export interface ExpandableNavItem {
  id: string;
  label: string;
  icon: React.ReactNode;
  items: ExpandableSubItem[];
  /**
   * Item-level visibility within a platform-scope group. Default (undefined) = visible to any platform
   * scope (Global Admin OR read-only Global Reader). "globalAdminOnly" = real Global Admin only — used for
   * platform-settings/mutation sub-sections that a read-only Global Reader must not see.
   */
  visibility?: "globalAdminOnly";
}

export interface NavGroup {
  id: string;
  label: string;
  items: NavItem[];
  visibility: "all" | "adminOrOperator" | "globalAdmin" | "fleet";
  style?: "global";
}

/** A group rendered as expandable GitHub-style items */
export interface ExpandableNavGroup {
  id: string;
  label: string;
  items: ExpandableNavItem[];
  visibility: "all" | "adminOrOperator" | "globalAdmin";
  style?: "global";
}

// --- Dashboard item (standalone, above all groups) ---

export const DASHBOARD_ITEM: NavItem = {
  id: "dashboard",
  label: "Dashboard",
  href: "/dashboard",
  icon: <HomeIcon />,
};

// --- Global navigation groups (flat items) ---

export const NAV_GROUPS: NavGroup[] = [
  {
    // Delegated ("MSP") admins manage a subset of tenants. This is their primary entry point; full
    // platform admins (GA/Reader) use the richer Global Admin section instead, so it is hidden for them
    // (see GlobalSidebar "fleet" visibility = delegated && !platform scope). The analytics items below are
    // the SAME pages a Global Admin sees, but the tenant switcher on each is bounded to the managed subset.
    id: "fleet",
    label: "Fleet",
    visibility: "fleet",
    items: [
      { id: "fleet-tenants", label: "Managed Tenants", href: "/fleet", icon: <GlobeAltIcon /> },
      { id: "fleet-fleet-health", label: "Fleet Health", href: "/fleet-health", icon: <ChartBarIcon /> },
      { id: "fleet-apps", label: "Software", href: "/apps", icon: <CubeIcon /> },
      { id: "fleet-geographic", label: "Geographic Perf.", href: "/geographic-performance", icon: <GlobeAltIcon /> },
      { id: "fleet-sla", label: "SLA Compliance", href: "/sla", icon: <ShieldCheckIcon /> },
      { id: "fleet-usage", label: "Usage Metrics", href: "/usage-metrics", icon: <TrendingUpIcon /> },
      { id: "fleet-audit", label: "Audit Log", href: "/audit", icon: <ClipboardDocumentIcon /> },
    ],
  },
  {
    id: "monitoring",
    label: "Monitoring",
    visibility: "adminOrOperator",
    items: [
      { id: "progress", label: "Progress Portal", href: "/progress", icon: <MonitorIcon /> },
      { id: "geographic-performance", label: "Geographic Perf.", href: "/geographic-performance", icon: <GlobeAltIcon /> },
      { id: "fleet-health", label: "Fleet Health", href: "/fleet-health", icon: <ChartBarIcon /> },
      { id: "sla", label: "SLA Compliance", href: "/sla", icon: <ShieldCheckIcon /> },
      { id: "apps", label: "Software", href: "/apps", icon: <CubeIcon /> },
      { id: "usage-metrics", label: "Usage Metrics", href: "/usage-metrics", icon: <TrendingUpIcon /> },
    ],
  },
  {
    id: "rules",
    label: "Rules",
    visibility: "adminOrOperator",
    items: [
      { id: "gather-rules", label: "Gather Rules", href: "/gather-rules", icon: <FolderIcon /> },
      { id: "analyze-rules", label: "Analyze Rules", href: "/analyze-rules", icon: <SparklesIcon /> },
      { id: "ime-log-patterns", label: "IME Log Patterns", href: "/ime-log-patterns", icon: <DocumentTextIcon /> },
    ],
  },
  {
    id: "operations",
    label: "Operations",
    visibility: "adminOrOperator",
    items: [
      { id: "audit", label: "Audit Log", href: "/audit", icon: <ClipboardDocumentIcon /> },
      { id: "health-check", label: "System Health", href: "/health-check", icon: <HeartIcon /> },
    ],
  },
];

// --- Expandable navigation groups (GitHub-style with sub-items) ---

export const EXPANDABLE_NAV_GROUPS: ExpandableNavGroup[] = [
  {
    id: "configuration",
    label: "Configuration",
    visibility: "adminOrOperator",
    items: [
      {
        id: "cfg-tenant", label: "Tenant", icon: <BuildingOfficeIcon />,
        items: [
          { id: "cfg-access-mgmt", label: "Access Management", href: "/settings/tenant/access-management" },
          { id: "cfg-autopilot", label: "Autopilot Validation", href: "/settings/tenant/autopilot" },
          { id: "cfg-hardware", label: "Hardware Whitelist", href: "/settings/tenant/hardware-whitelist" },
          { id: "cfg-notifications", label: "Notifications", href: "/settings/tenant/notifications" },
          { id: "cfg-sla-targets", label: "SLA Targets", href: "/settings/tenant/sla-targets" },
          { id: "cfg-bootstrap-sessions", label: "Bootstrap Sessions", href: "/settings/tenant/bootstrap-sessions" },
          { id: "cfg-graph-permissions", label: "Optional Graph capabilities", href: "/settings/tenant/graph-permissions" },
          { id: "cfg-plan", label: "Plan", href: "/settings/tenant/plan" },
          { id: "cfg-support", label: "Submit Logs", href: "/settings/tenant/support" },
        ],
      },
      {
        id: "cfg-agent", label: "Agent", icon: <GearIcon />,
        items: [
          { id: "cfg-agent-settings", label: "Agent Settings", href: "/settings/agent/settings" },
          { id: "cfg-agent-analyzers", label: "Agent Analyzers", href: "/settings/agent/analyzers" },
          { id: "cfg-diagnostics", label: "Diagnostics Package", href: "/settings/agent/diagnostics" },
          { id: "cfg-agent-unrestricted", label: "Unrestricted Mode", href: "/settings/agent/unrestricted-mode" },
        ],
      },
      {
        id: "cfg-maintenance", label: "Maintenance", icon: <WrenchScrewdriverIcon />,
        items: [
          { id: "cfg-data", label: "Data Management", href: "/settings/management/data" },
          { id: "cfg-offboarding", label: "Offboarding", href: "/settings/management/offboarding" },
        ],
      },
      {
        id: "cfg-reporting", label: "Reporting", icon: <ChartBarIcon />,
        items: [
          { id: "cfg-mcp-usage", label: "MCP Usage", href: "/settings/reporting/mcp-usage" },
        ],
      },
    ],
  },
  {
    id: "global-admin",
    label: "Global Admin",
    visibility: "globalAdmin",
    style: "global",
    items: [
      {
        id: "ga-tenants", label: "Tenants", icon: <BuildingOfficeIcon />,
        items: [
          { id: "ga-tenant-mgmt", label: "Tenant Management", href: "/admin/tenants/management" },
          { id: "ga-config-report", label: "Config Report", href: "/admin/tenants/config-report" },
        ],
      },
      {
        id: "ga-metrics", label: "Metrics", icon: <ChartBarIcon />,
        items: [
          { id: "ga-platform-metrics", label: "Platform Metrics", href: "/admin/metrics/platform-metrics" },
          { id: "ga-usage", label: "Platform Usage", href: "/admin/metrics/usage" },
          { id: "ga-active-users", label: "Active Users", href: "/admin/presence" },
          { id: "ga-mcp-usage", label: "MCP Usage", href: "/admin/metrics/mcp-usage" },
        ],
      },
      {
        id: "ga-reports", label: "Reports", icon: <DocumentTextIcon />,
        items: [
          { id: "ga-session-reports", label: "Session Reports", href: "/admin/reports/session-reports" },
          { id: "ga-distress-reports", label: "Distress Reports", href: "/admin/reports/distress-reports" },
          { id: "ga-user-feedback", label: "User Feedback", href: "/admin/reports/user-feedback" },
          { id: "ga-session-export", label: "Session Export", href: "/admin/reports/session-export" },
        ],
      },
      {
        id: "ga-security", label: "Security", icon: <ShieldCheckIcon />,
        items: [
          { id: "ga-device-block", label: "Device Block", href: "/admin/security/device-block" },
          { id: "ga-version-block", label: "Version Block", href: "/admin/security/version-block" },
          { id: "ga-vulnerability", label: "Vulnerability Data", href: "/admin/security/vulnerability-data" },
        ],
      },
      {
        // Platform settings are GA-only — a read-only Global Reader must not see them (their secrets
        // are mutation surfaces; the reader gets redacted/read-only tenant config elsewhere).
        id: "ga-settings", label: "Settings", icon: <GearIcon />, visibility: "globalAdminOnly",
        items: [
          { id: "ga-global", label: "Global Settings", href: "/admin/settings/global" },
          { id: "ga-diag-paths", label: "Diagnostics Log Paths", href: "/admin/settings/diagnostics-log-paths" },
          { id: "ga-mcp-users", label: "MCP Users", href: "/admin/settings/mcp-users" },
          { id: "ga-delegated-admins", label: "Delegated Admins", href: "/admin/settings/delegated-admins" },
          { id: "ga-tenant-groups", label: "Tenant Groups", href: "/admin/settings/tenant-groups" },
          { id: "ga-config-reseed", label: "Config Reseed", href: "/admin/settings/config-reseed" },
          { id: "ga-usage-plans", label: "Usage Plans", href: "/admin/settings/usage-plans" },
        ],
      },
      {
        // Ops + Backups + Customs Archive are destructive platform operations — GA-only, hidden from a
        // read-only Global Reader (routes are also gated via requireGlobalAdmin layouts).
        id: "ga-ops", label: "Ops", icon: <WrenchIcon />, visibility: "globalAdminOnly",
        items: [
          { id: "ga-maintenance", label: "Maintenance", href: "/admin/ops" },
          { id: "ga-session-cleanup", label: "Session Cleanup", href: "/admin/ops/session-cleanup" },
          { id: "ga-customs-archive", label: "Customs Archive", href: "/admin/customs-archive" },
          { id: "ga-backups", label: "Backups", href: "/admin/backups" },
        ],
      },
      {
        // Software mapping is vulnerability-data curation (write-heavy CPE mapping/ignore) — GA-only,
        // hidden from a read-only Global Reader (route also gated via requireGlobalAdmin layout).
        id: "ga-software", label: "Software", icon: <GlobeAltIcon />, visibility: "globalAdminOnly",
        items: [
          { id: "ga-software-mapping", label: "Software Mapping", href: "/admin/software" },
        ],
      },
    ],
  },
];

// --- Regular user nav (minimal) ---

export const REGULAR_USER_ITEMS: NavItem[] = [
  { id: "progress", label: "Progress Portal", href: "/progress", icon: <MonitorIcon /> },
];
