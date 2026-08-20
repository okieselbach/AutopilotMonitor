export interface EnrollmentEvent {
  eventId: string;
  sessionId: string;
  timestamp: string;
  eventType: string;
  severity: string;
  source: string;
  phase: number;
  phaseName?: string;
  message: string;
  sequence: number;
  receivedAt?: string;
  data?: Record<string, unknown>;
  /** True when ingest clamped an out-of-range device timestamp to server time. */
  timestampClamped?: boolean;
  /** The pre-clamp device timestamp; only set when timestampClamped is true. */
  originalTimestamp?: string | null;
}

export interface RuleResult {
  resultId: string;
  sessionId: string;
  tenantId: string;
  ruleId: string;
  ruleTitle: string;
  severity: string;
  category: string;
  confidenceScore: number;
  explanation: string;
  remediation: { title: string; steps: string[] }[];
  relatedDocs: { title: string; url: string }[];
  matchedConditions: Record<string, unknown>;
  detectedAt: string;
  // Evaluation lifecycle (evaluateOn interim triggers) — absent on legacy rows.
  firstDetectedAt?: string | null;
  lastEvaluatedAt?: string | null;
  isInterim?: boolean;
  resolvedAt?: string | null;
  notifiedAt?: string | null;
}
