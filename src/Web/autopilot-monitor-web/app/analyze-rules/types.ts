export interface RelatedDoc {
  title: string;
  url: string;
}

export interface RemediationStep {
  title: string;
  steps: string[];
}

export interface ConfidenceFactor {
  signal: string;
  condition: string;
  weight: number;
}

export interface RuleCondition {
  signal: string;
  source: string;
  eventType: string;
  dataField: string;
  operator: string;
  value: string;
  required: boolean;
  // event_data_array: sub-field tested on each array element (e.g. "identity")
  itemField?: string;
  // event_count: optional value filter applied before counting
  filterField?: string;
  filterOperator?: string;
  filterValue?: string;
  // optional suppression by a resolving event (same joinField value)
  suppressByEvent?: { eventType: string; joinField: string } | null;
  // event_correlation fields
  correlateEventType?: string;
  joinField?: string;
  timeWindowSeconds?: number | null;
  eventAFilterField?: string;
  eventAFilterOperator?: string;
  eventAFilterValue?: string;
  // clock_skew: which device-clock metric to evaluate ("clock_jump" | "sustained_offset");
  // value is the threshold in seconds, operator is limited to gt/gte on the magnitude
  skewMetric?: string;
}

export interface RulePrecondition {
  source: string;
  eventType: string;
  dataField: string;
  operator: string;
  value: string;
  description?: string;
}

export interface TemplateVariable {
  name: string;
  label: string;
  description?: string;
  conditionIndex: number;
  field: string;
  placeholder: string;
  validation?: string;
}

export interface AnalyzeRule {
  ruleId: string;
  title: string;
  description: string;
  severity: string;
  category: string;
  trigger: string;
  version: string;
  author: string;
  enabled: boolean;
  isBuiltIn: boolean;
  isCommunity: boolean;
  preconditions?: RulePrecondition[];
  conditions: RuleCondition[];
  baseConfidence: number;
  confidenceFactors: ConfidenceFactor[];
  confidenceThreshold: number;
  explanation: string;
  remediation: RemediationStep[];
  relatedDocs: RelatedDoc[];
  tags: string[];
  templateVariables?: TemplateVariable[];
  derivedFromTemplateRuleId?: string;
  /** Rule-definition default for "fire → mark the whole session as failed". */
  markSessionAsFailedDefault?: boolean;
  /** Tenant override. `undefined`/`null` = inherit the default. */
  markSessionAsFailed?: boolean | null;
  /** Rule-definition default for "fire → send channel notification" (false for all shipped rules). */
  notifyDefault?: boolean;
  /** Tenant override. `undefined`/`null` = inherit the default. */
  notify?: boolean | null;
  /** Tenant notification-channel ids targeted when this rule fires (requires effective notify). */
  notifyChannelIds?: string[];
  /**
   * When the rule is evaluated. Absent/empty = ["enrollment_end"] (terminal-only default).
   * Interim triggers: "whiteglove_sealed" and "on_event:<eventType>".
   */
  evaluateOn?: string[] | null;
  createdAt: string;
  updatedAt: string;
}

export interface RuleForm {
  ruleId: string;
  title: string;
  description: string;
  severity: string;
  category: string;
  trigger: string;
  explanation: string;
  baseConfidence: number;
  confidenceThreshold: number;
  preconditions: RulePrecondition[];
  conditions: RuleCondition[];
  confidenceFactors: ConfidenceFactor[];
  remediation: RemediationStep[];
  relatedDocs: RelatedDoc[];
  /** Evaluation triggers — structured form mirror of AnalyzeRule.evaluateOn. */
  evalAtEnrollmentEnd: boolean;
  evalAtWhitegloveSealed: boolean;
  /** Comma-separated event types for on_event:<type> interim triggers. */
  evalOnEventTypes: string;
}

export const CATEGORIES = ["network", "identity", "apps", "device", "esp", "enrollment"] as const;
export const SEVERITIES = ["info", "warning", "high", "critical"] as const;
export const TRIGGERS = ["single", "correlation"] as const;
export const OPERATORS = ["equals", "not_equals", "contains", "not_contains", "regex", "not_regex", "gt", "lt", "gte", "lte", "exists", "not_exists", "count_gte", "count_per_group_gte", "in", "not_in"] as const;
export const SOURCES = ["event_type", "event_data", "event_data_array", "phase_duration", "app_install_duration", "event_count", "event_correlation", "clock_skew"] as const;
export const SKEW_METRICS = ["clock_jump", "sustained_offset"] as const;
export const PRECONDITION_OPERATORS = ["equals", "not_equals", "contains", "not_contains", "regex", "not_regex", "gt", "lt", "gte", "lte", "exists", "not_exists", "in", "not_in"] as const;

export const SEVERITY_COLORS: Record<string, { bg: string; text: string; border: string; dot: string }> = {
  critical: { bg: "bg-red-100", text: "text-red-800", border: "border-red-300", dot: "bg-red-500" },
  high: { bg: "bg-orange-100", text: "text-orange-800", border: "border-orange-300", dot: "bg-orange-500" },
  warning: { bg: "bg-yellow-100", text: "text-yellow-800", border: "border-yellow-300", dot: "bg-yellow-500" },
  info: { bg: "bg-blue-100", text: "text-blue-800", border: "border-blue-300", dot: "bg-blue-500" },
};

export const CATEGORY_COLORS: Record<string, { bg: string; text: string }> = {
  network: { bg: "bg-blue-100", text: "text-blue-700" },
  identity: { bg: "bg-purple-100", text: "text-purple-700" },
  apps: { bg: "bg-orange-100", text: "text-orange-700" },
  device: { bg: "bg-gray-100", text: "text-gray-700" },
  esp: { bg: "bg-teal-100", text: "text-teal-700" },
  enrollment: { bg: "bg-indigo-100", text: "text-indigo-700" },
};

export function getSeverityColor(severity: string) {
  return SEVERITY_COLORS[severity.toLowerCase()] || SEVERITY_COLORS.info;
}

export function getCategoryColor(category: string) {
  return CATEGORY_COLORS[category.toLowerCase()] || { bg: "bg-gray-100", text: "text-gray-700" };
}

export const EMPTY_CONDITION: RuleCondition = {
  signal: "",
  source: "event_type",
  eventType: "",
  dataField: "",
  operator: "contains",
  value: "",
  required: true,
  itemField: "",
  correlateEventType: "",
  joinField: "",
  timeWindowSeconds: null,
  eventAFilterField: "",
  eventAFilterOperator: "equals",
  eventAFilterValue: "",
  // skewMetric deliberately absent: it is an enum field ("clock_jump" | "sustained_offset")
  // and an empty string would violate the schema — the source select defaults it when
  // switching a condition to clock_skew.
};

export const EMPTY_FACTOR: ConfidenceFactor = {
  signal: "",
  condition: "",
  weight: 10,
};

export const EMPTY_PRECONDITION: RulePrecondition = {
  source: "event_data",
  eventType: "",
  dataField: "",
  operator: "equals",
  value: "",
  description: "",
};

export const EMPTY_FORM: RuleForm = {
  ruleId: "",
  title: "",
  description: "",
  severity: "warning",
  category: "device",
  trigger: "single",
  explanation: "",
  baseConfidence: 50,
  confidenceThreshold: 40,
  preconditions: [],
  conditions: [{ ...EMPTY_CONDITION }],
  confidenceFactors: [],
  remediation: [],
  relatedDocs: [],
  evalAtEnrollmentEnd: true,
  evalAtWhitegloveSealed: false,
  evalOnEventTypes: "",
};

export function ruleToForm(rule: AnalyzeRule): RuleForm {
  const triggers = rule.evaluateOn && rule.evaluateOn.length > 0 ? rule.evaluateOn : ["enrollment_end"];
  return {
    ruleId: rule.ruleId,
    title: rule.title,
    description: rule.description || "",
    severity: rule.severity,
    category: rule.category,
    trigger: rule.trigger || "single",
    explanation: rule.explanation || "",
    baseConfidence: rule.baseConfidence,
    confidenceThreshold: rule.confidenceThreshold,
    preconditions: (rule.preconditions ?? []).map(p => ({ ...p })),
    conditions: rule.conditions.length > 0 ? rule.conditions.map(c => ({ ...c })) : [{ ...EMPTY_CONDITION }],
    confidenceFactors: rule.confidenceFactors.map(f => ({ ...f })),
    remediation: rule.remediation.map(r => ({ title: r.title, steps: [...r.steps] })),
    relatedDocs: rule.relatedDocs.map(d => ({ ...d })),
    evalAtEnrollmentEnd: triggers.some(t => t.toLowerCase() === "enrollment_end"),
    evalAtWhitegloveSealed: triggers.some(t => t.toLowerCase() === "whiteglove_sealed"),
    evalOnEventTypes: triggers
      .filter(t => t.toLowerCase().startsWith("on_event:"))
      .map(t => t.slice("on_event:".length))
      .join(", "),
  };
}

/**
 * Builds the evaluateOn payload from the structured form fields. Returns undefined when the
 * selection equals the default (enrollment_end only) so unchanged rules stay clean of the field.
 */
export function formToEvaluateOn(form: RuleForm): string[] | undefined {
  const triggers: string[] = [];
  if (form.evalAtEnrollmentEnd) triggers.push("enrollment_end");
  if (form.evalAtWhitegloveSealed) triggers.push("whiteglove_sealed");
  for (const raw of form.evalOnEventTypes.split(",")) {
    const eventType = raw.trim().toLowerCase();
    if (eventType) triggers.push(`on_event:${eventType}`);
  }
  if (triggers.length === 1 && triggers[0] === "enrollment_end") return undefined;
  return triggers.length > 0 ? triggers : undefined;
}

/**
 * Client-side pre-flight for the on_event list (the backend enforces the same rules on save):
 * lowercase snake_case event types, none of the hard-blocked high-frequency telemetry types.
 * Returns an error message or null.
 */
export function validateOnEventTypes(raw: string, blockedTypes: readonly string[]): string | null {
  const types = raw.split(",").map(t => t.trim().toLowerCase()).filter(t => t.length > 0);
  const malformed = types.filter(t => !/^[a-z0-9_]{1,128}$/.test(t));
  if (malformed.length > 0) {
    return `Invalid event type(s): ${malformed.join(", ")} — use lowercase snake_case (e.g. hybrid_login_pending).`;
  }
  const blocked = types.filter(t => blockedTypes.includes(t));
  if (blocked.length > 0) {
    return `Blocked high-frequency event type(s): ${blocked.join(", ")} — these occur on nearly every batch and cannot be used as interim triggers.`;
  }
  return null;
}

/** Human-readable interim-trigger labels for a rule card badge (empty when terminal-only). */
export function interimTriggerLabels(rule: AnalyzeRule): string[] {
  const triggers = rule.evaluateOn ?? [];
  const labels: string[] = [];
  for (const t of triggers) {
    const lower = t.toLowerCase();
    if (lower === "whiteglove_sealed") labels.push("WhiteGlove seal");
    else if (lower.startsWith("on_event:")) labels.push(`on ${t.slice("on_event:".length)}`);
  }
  return labels;
}
