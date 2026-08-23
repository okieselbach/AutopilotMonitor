export const AGENT_NAV_SECTIONS = [
  { id: "settings", label: "Agent Settings", description: "Agent collector and behavior configuration" },
  { id: "analyzers", label: "Agent Analyzers", description: "Security checks: local admin, integrity bypass, software inventory, OOBE console" },
  { id: "diagnostics", label: "Diagnostics Package", description: "Diagnostics upload and log path configuration" },
  { id: "unrestricted-mode", label: "Unrestricted Mode", description: "Unrestricted mode configuration" },
] as const;

export type AgentSectionId = (typeof AGENT_NAV_SECTIONS)[number]["id"];
