export interface GatherRule {
  ruleId: string;
  title: string;
  description: string;
  category: string;
  version: string;
  author: string;
  enabled: boolean;
  isBuiltIn: boolean;
  isCommunity: boolean;
  collectorType: string;
  target: string;
  parameters: Record<string, string>;
  trigger: string;
  intervalSeconds: number | null;
  triggerPhase: string | null;
  triggerEventType: string | null;
  activePhases?: string[] | null;
  activeFromPhase?: string | null;
  emitMode?: string | null;
  outputEventType: string;
  outputSeverity: string;
  tags: string[];
  createdAt: string;
  updatedAt: string;
}

export interface NewRuleForm {
  ruleId: string;
  title: string;
  description: string;
  category: string;
  collectorType: string;
  target: string;
  valueName: string;
  listSubkeys: boolean;
  eventId: string;
  messageFilter: string;
  maxEntries: string;
  source: string;
  readContent: boolean;
  logPattern: string;
  logFormat: string;
  trackPosition: boolean;
  maxLines: string;
  jsonPath: string;
  xpath: string;
  xmlNamespaces: string;
  maxResults: string;
  trigger: string;
  intervalSeconds: number;
  triggerPhase: string;
  triggerEventType: string;
  scopeMode: "always" | "during" | "from";
  activePhases: string[];
  activeFromPhase: string;
  emitMode: string;
  outputEventType: string;
  outputSeverity: string;
}

export const CATEGORIES = ["network", "identity", "apps", "device", "esp", "enrollment"] as const;
export const COLLECTOR_TYPES = ["registry", "eventlog", "wmi", "file", "command_allowlisted", "logparser", "json", "xml"] as const;
export const TRIGGERS = ["startup", "phase_change", "interval", "on_event"] as const;
export const SEVERITIES = ["info", "warning", "error", "critical"] as const;

// Canonical phase-scope tokens: the backend EnrollmentPhase enum NAMES from Start(0) through
// Complete(7) — Unknown/Failed are not selectable. Local mirror (like eventTypes.ts); display
// labels are gather-rule-specific, deliberately NOT reusing phaseConstants.ts timeline names.
export const GATHER_PHASES: ReadonlyArray<{ value: string; label: string }> = [
  { value: "Start", label: "Start" },
  { value: "DevicePreparation", label: "Device Preparation" },
  { value: "DeviceSetup", label: "Device Setup" },
  { value: "AppsDevice", label: "Apps (Device)" },
  { value: "AccountSetup", label: "Account Setup" },
  { value: "AppsUser", label: "Apps (User)" },
  { value: "FinalizingSetup", label: "Finalizing Setup" },
  { value: "Complete", label: "Complete" },
];

export const EMIT_MODES: ReadonlyArray<{ value: string; label: string }> = [
  { value: "always", label: "Always (emit every collection)" },
  { value: "on_change", label: "On change (emit only when the result changes)" },
];

/** Display label for a phase-scope token; falls back to the raw token for unknown values. */
export function formatGatherPhase(token: string): string {
  return GATHER_PHASES.find((p) => p.value === token)?.label ?? token;
}

export const CATEGORY_COLORS: Record<string, { bg: string; text: string }> = {
  network: { bg: "bg-blue-100", text: "text-blue-700" },
  identity: { bg: "bg-purple-100", text: "text-purple-700" },
  apps: { bg: "bg-orange-100", text: "text-orange-700" },
  device: { bg: "bg-gray-100", text: "text-gray-700" },
  esp: { bg: "bg-teal-100", text: "text-teal-700" },
  enrollment: { bg: "bg-indigo-100", text: "text-indigo-700" },
};

export const COLLECTOR_TYPE_LABELS: Record<string, string> = {
  registry: "Registry",
  eventlog: "Event Log",
  wmi: "WMI Query",
  file: "File",
  command_allowlisted: "Command (Allowlisted)",
  command: "Command (Allowlisted)",
  logparser: "Log Parser",
  json: "JSON (JSONPath)",
  xml: "XML (XPath)",
};

export const TARGET_PLACEHOLDERS: Record<string, string> = {
  registry: "e.g., HKLM\\SOFTWARE\\Microsoft\\Enrollments",
  eventlog: "e.g., Microsoft-Windows-Shell-Core/Operational",
  wmi: "e.g., SELECT * FROM Win32_BIOS",
  file: "e.g., C:\\Windows\\Panther\\UnattendGC\\setupact.log",
  command_allowlisted: "e.g., Get-Tpm or dsregcmd /status",
  logparser: "e.g., %ProgramData%\\Microsoft\\IntuneManagementExtension\\Logs\\AppWorkload*.log",
  json: "e.g., C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\config.json",
  xml: "e.g., C:\\Windows\\Panther\\unattend.xml",
};

export const TARGET_HINTS: Record<string, string> = {
  registry: "Full registry path including hive (HKLM, HKCU). The agent reads values from this key.",
  eventlog: "Event log name — supports operational/analytic logs like Microsoft-Windows-Shell-Core/Operational.",
  wmi: "Full WQL query (SELECT * FROM ...). Must use an allowed WMI class.",
  file: "File path. Environment variables like %ProgramData% are supported. Must be within allowed directories.",
  command_allowlisted: "Exact command string from the agent's allowlist. Custom commands are not permitted.",
  logparser: "Path to a log file. Supports wildcards (* and ?) in the filename, e.g. AppWorkload-*.log. Environment variables are expanded.",
  json: "Path to a JSON file. Environment variables supported. Must be within allowed directories. Use JSONPath to extract values.",
  xml: "Path to an XML file. Environment variables supported. Must be within allowed directories. Use XPath to extract values.",
};

export const EMPTY_FORM: NewRuleForm = {
  ruleId: "",
  title: "",
  description: "",
  category: "device",
  collectorType: "registry",
  target: "",
  valueName: "",
  listSubkeys: false,
  eventId: "",
  messageFilter: "",
  maxEntries: "",
  source: "",
  readContent: false,
  logPattern: "",
  logFormat: "cmtrace",
  trackPosition: true,
  maxLines: "",
  jsonPath: "",
  xpath: "",
  xmlNamespaces: "",
  maxResults: "",
  trigger: "startup",
  intervalSeconds: 60,
  triggerPhase: "",
  triggerEventType: "",
  scopeMode: "always",
  activePhases: [],
  activeFromPhase: "",
  // New rules default to on_change (anti-spam); existing rules load as "always" in startEditing.
  emitMode: "on_change",
  outputEventType: "",
  outputSeverity: "info",
};

export function formatTrigger(trigger: string) {
  switch (trigger) {
    case "phase_change": return "Phase Change";
    case "on_event": return "On Event";
    default: return trigger.charAt(0).toUpperCase() + trigger.slice(1);
  }
}

/**
 * Normalizes a form object after JSON-mode merges. Pasted JSON is usually rule-shaped
 * (activePhases/activeFromPhase/emitMode, no scopeMode key), so the UI-only scopeMode must
 * be derived from the data — otherwise the create/save payload would silently drop the
 * scope fields. Also coerces null/unknown emitMode values to the "always" select option.
 */
export function withDerivedScopeMode(form: NewRuleForm): NewRuleForm {
  const activePhases = (Array.isArray(form.activePhases) ? form.activePhases : [])
    .filter((p): p is string => typeof p === "string" && p.length > 0);
  const activeFromPhase = typeof form.activeFromPhase === "string" ? form.activeFromPhase : "";
  const scopeMode: NewRuleForm["scopeMode"] =
    activePhases.length > 0 ? "during" : activeFromPhase ? "from" : "always";
  const emitMode = form.emitMode === "on_change" ? "on_change" : "always";
  return { ...form, activePhases, activeFromPhase, scopeMode, emitMode };
}
