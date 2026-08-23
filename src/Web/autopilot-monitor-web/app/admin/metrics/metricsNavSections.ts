export const METRICS_NAV_SECTIONS = [
  { id: "platform-metrics", label: "Platform Metrics", description: "Agent performance, delivery latency, crash rates, and platform health" },
  { id: "usage", label: "Platform Usage", description: "Platform usage statistics across all tenants" },
  { id: "mcp-usage", label: "MCP Usage", description: "MCP API usage metrics across all users" },
  { id: "verdict-calibration", label: "Verdict Calibration", description: "Which code path produced each session verdict, overrides and re-enrollment — classifier diagnostics" },
] as const;

export type MetricsSectionId = (typeof METRICS_NAV_SECTIONS)[number]["id"];
