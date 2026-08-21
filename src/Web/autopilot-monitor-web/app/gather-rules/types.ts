import { validateGatherRuleTarget } from "@/utils/guardValidation";

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
  tags: string[];
}

export const CATEGORIES = ["network", "identity", "apps", "device", "esp", "enrollment"] as const;
export const COLLECTOR_TYPES = ["registry", "eventlog", "wmi", "file", "command_allowlisted", "logparser", "json", "xml"] as const;
export const TRIGGERS = ["startup", "phase_change", "phase_exit", "interval", "on_event"] as const;

/** Triggers that collect at a phase boundary and therefore use the triggerPhase field. */
export const PHASE_TRIGGERS: ReadonlyArray<string> = ["phase_change", "phase_exit"];
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
  wmi: "Full WQL query — SELECT * or a comma-separated property list from an allowed WMI class, e.g. SELECT BatteryStatus FROM Win32_Battery. Pairs well with Emit Mode \"On change\": polling a single property only emits when that property changes.",
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
  tags: [],
};

export function formatTrigger(trigger: string) {
  switch (trigger) {
    case "phase_change": return "Phase Start";
    case "phase_exit": return "Phase End";
    case "on_event": return "On Event";
    default: return trigger.charAt(0).toUpperCase() + trigger.slice(1);
  }
}

/**
 * True when the trigger already pins the rule to a single firing: Startup, or a phase
 * trigger naming a CONCRETE phase (Phase Start / Phase End dedup per rule+phase). The
 * any-phase variants, Interval and On Event can all fire repeatedly.
 */
export function firesExactlyOnce(form: Pick<NewRuleForm, "trigger" | "triggerPhase">): boolean {
  if (form.trigger === "startup") return true;
  return PHASE_TRIGGERS.includes(form.trigger) && !!form.triggerPhase;
}

/**
 * Whether phase scope can change anything for this trigger. It cannot when the trigger
 * names a concrete phase — that phase IS the moment, so a scope could only ever suppress
 * the single firing, never move it. Startup keeps it (scope defers the one-shot until the
 * scope activates); Interval / On Event / any-phase variants are its main use.
 */
export function supportsPhaseScope(form: Pick<NewRuleForm, "trigger" | "triggerPhase">): boolean {
  return !(PHASE_TRIGGERS.includes(form.trigger) && !!form.triggerPhase);
}

/** Emit mode dedups repeated results — meaningless for a rule that fires exactly once. */
export function supportsEmitMode(form: Pick<NewRuleForm, "trigger" | "triggerPhase">): boolean {
  return !firesExactlyOnce(form);
}

/**
 * True when a CUSTOM rule's target fails guardrail validation — the agent would block
 * it on every device (security_warning, no data), so the portal refuses to enable it.
 * Built-in/community rules are catalog-validated and never gated; unrestrictedMode is
 * respected because the validator returns allowed=true for it.
 */
export function targetBlocked(
  rule: Pick<GatherRule, "collectorType" | "target"> & Partial<Pick<GatherRule, "isBuiltIn" | "isCommunity">>,
  unrestrictedMode: boolean
): boolean {
  if (rule.isBuiltIn || rule.isCommunity) return false;
  if (!rule.target?.trim()) return false;
  // null = collector type the validator doesn't know — no verdict, don't gate.
  const result = validateGatherRuleTarget(rule.collectorType, rule.target, unrestrictedMode);
  return result !== null && !result.allowed;
}

/**
 * Rejects a scope mode that was selected but left unfilled. Without this the payload
 * silently sends null (= unrestricted) and the rule runs everywhere while the form still
 * reads "From a phase onwards" — the exact trap of a mode dropdown with an empty detail.
 */
export function validateScopeSelection(form: NewRuleForm): string | null {
  if (!supportsPhaseScope(form)) return null;
  if (form.scopeMode === "during" && form.activePhases.length === 0)
    return 'Select at least one phase under "Active Phases", or set "Active During" back to "All phases".';
  if (form.scopeMode === "from" && !form.activeFromPhase)
    return 'Select a phase under "Active From Phase", or set "Active During" back to "All phases".';
  return null;
}

/**
 * Scope + emit fields for the create/save payload. Controls the form hides for the current
 * trigger must not leak stale state from an earlier selection, so they are nulled here
 * rather than at each call site.
 */
export function buildScopeFields(form: NewRuleForm) {
  const scoped = supportsPhaseScope(form);
  return {
    activePhases: scoped && form.scopeMode === "during" && form.activePhases.length > 0 ? form.activePhases : null,
    activeFromPhase: scoped && form.scopeMode === "from" && form.activeFromPhase ? form.activeFromPhase : null,
    emitMode: supportsEmitMode(form) ? (form.emitMode || null) : null,
  };
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

/**
 * What JSON-mode paste can contain: the serialized form, a rule export, or a mix.
 * Shared fields take the rule's (nullable) typing so a GatherRule assigns directly;
 * the form-only flat fields come from NewRuleForm.
 */
export type PastedGatherJson = Partial<GatherRule> & Partial<Omit<NewRuleForm, keyof GatherRule>>;

/**
 * Maps rule-shaped OR form-shaped JSON into a NewRuleForm. Rule exports carry a nested
 * `parameters` object (collector options) while the form stores them as flat fields —
 * without this unflattening, pasting an export into the JSON editor silently dropped
 * every collector parameter. When `parameters` is present it is authoritative and the
 * flat fields are ignored (they can only be stale merge leftovers); form-shaped JSON
 * (no `parameters` key — the shape the JSON toggle serializes) keeps its flat fields.
 * Also the single mapper behind startEditing, so edit and paste stay equivalent.
 */
export function gatherRuleToForm(input: PastedGatherJson): NewRuleForm {
  const p = input.parameters && typeof input.parameters === "object" ? input.parameters : null;
  return withDerivedScopeMode({
    ...EMPTY_FORM,
    ruleId: input.ruleId ?? "",
    title: input.title ?? "",
    description: input.description ?? "",
    category: input.category ?? EMPTY_FORM.category,
    collectorType: input.collectorType ?? EMPTY_FORM.collectorType,
    target: input.target ?? "",
    valueName: p ? p.valueName || "" : input.valueName ?? "",
    listSubkeys: p ? p.listSubkeys === "true" : input.listSubkeys ?? false,
    eventId: p ? p.eventId || "" : input.eventId ?? "",
    messageFilter: p ? p.messageFilter || "" : input.messageFilter ?? "",
    maxEntries: p ? p.maxEntries || "" : input.maxEntries ?? "",
    source: p ? p.source || "" : input.source ?? "",
    readContent: p ? p.readContent === "true" : input.readContent ?? false,
    logPattern: p ? p.pattern || "" : input.logPattern ?? "",
    logFormat: p ? p.format || "cmtrace" : input.logFormat ?? "cmtrace",
    trackPosition: p ? p.trackPosition !== "false" : input.trackPosition ?? true,
    maxLines: p ? p.maxLines || "" : input.maxLines ?? "",
    jsonPath: p ? p.jsonpath || "" : input.jsonPath ?? "",
    xpath: p ? p.xpath || "" : input.xpath ?? "",
    xmlNamespaces: p ? p.namespaces || "" : input.xmlNamespaces ?? "",
    maxResults: p ? p.maxResults || "" : input.maxResults ?? "",
    trigger: input.trigger ?? EMPTY_FORM.trigger,
    intervalSeconds: input.intervalSeconds || 60,
    triggerPhase: input.triggerPhase ?? "",
    triggerEventType: input.triggerEventType ?? "",
    activePhases: input.activePhases ?? [],
    activeFromPhase: input.activeFromPhase ?? "",
    // A rule without the field behaves "always"; only an explicit on_change survives —
    // the on_change default is for rules started from the empty form, not for imports.
    emitMode: input.emitMode ?? "always",
    outputEventType: input.outputEventType ?? "",
    outputSeverity: input.outputSeverity ?? EMPTY_FORM.outputSeverity,
    tags: Array.isArray(input.tags) ? input.tags.filter((t): t is string => typeof t === "string") : [],
  });
}
