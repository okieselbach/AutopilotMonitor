export const METRICS_NAV_SECTIONS = [
  { id: "customers", label: "Customers", description: "New tenants per day and the growth over time" },
  { id: "platform-metrics", label: "Platform Metrics", description: "Agent performance, delivery latency, crash rates, and platform health" },
  { id: "usage", label: "Platform Usage", description: "Platform usage statistics across all tenants" },
  { id: "tenant-activity", label: "Tenant Activity", description: "Which tenants enroll how much, against the window before, and who is new, declining, dormant or never enrolled" },
  { id: "mcp-usage", label: "MCP Usage", description: "MCP usage by tenant, account and tool, and where the limits bite" },
  { id: "verdict-calibration", label: "Verdict Calibration", description: "Which code path produced each session verdict, overrides and re-enrollment — classifier diagnostics" },
  { id: "ime-pattern-health", label: "IME Pattern Health", description: "Which IME log patterns still match on which IME version — pattern-drift alerts and tracker health" },
] as const;

export type MetricsSectionId = (typeof METRICS_NAV_SECTIONS)[number]["id"];
