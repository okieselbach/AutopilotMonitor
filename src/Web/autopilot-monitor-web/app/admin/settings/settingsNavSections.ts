export const SETTINGS_NAV_SECTIONS = [
  { id: "global", label: "Global Settings", description: "Global platform configuration" },
  { id: "diagnostics-log-paths", label: "Diagnostics Log Paths", description: "Global diagnostics log path configuration" },
  { id: "mcp-users", label: "MCP Users", description: "Manage AI agent access via MCP" },
  { id: "delegated-admins", label: "Delegated Admins", description: "Grant cross-tenant read access (MSP mode)" },
  { id: "tenant-groups", label: "Tenant Groups", description: "Group tenants and assign delegated admins (MSP mode)" },
  { id: "config-reseed", label: "Config Reseed", description: "Fetch and reseed rules from GitHub" },
  { id: "usage-plans", label: "Usage Plans", description: "Define MCP usage plan tiers and limits" },
  { id: "alerts", label: "Alerts", description: "Configure ops event alert rules and notification providers" },
] as const;

export type SettingsSectionId = (typeof SETTINGS_NAV_SECTIONS)[number]["id"];
