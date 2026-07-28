/**
 * Wire types for the Inspector v1 (Plan §M6). Mirror the shapes returned by
 * `/api/sessions/{id}/decision-graph` and `/api/sessions/{id}/signals` — kept
 * in this folder so the rest of the web app doesn't need to know about them.
 *
 * Field names use camelCase to match the ASP.NET JSON serializer default.
 */

export interface DecisionGraphNode {
  id: string;
  isTerminal: boolean;
  /** "Succeeded" | "Failed" | "PausedForPart2" | null */
  terminalOutcome: string | null;
  visitCount: number;
}

export interface DecisionGraphEdge {
  stepIndex: number;
  fromStage: string;
  toStage: string;
  trigger: string;
  /** false = dead-end (guard blocked the transition). */
  taken: boolean;
  deadEndReason: string | null;
  signalOrdinalRef: number;
  occurredAtUtc: string;
  classifierVerdictId: string | null;
  classifierHypothesisLevel: string | null;
}

export interface DecisionGraphProjection {
  tenantId: string;
  sessionId: string;
  nodes: DecisionGraphNode[];
  edges: DecisionGraphEdge[];
  reducerVersion: string;
}

export interface DecisionGraphResponse {
  success: boolean;
  truncated: boolean;
  graph: DecisionGraphProjection;
}

export interface SignalRecord {
  tenantId: string;
  sessionId: string;
  sessionSignalOrdinal: number;
  sessionTraceOrdinal: number;
  kind: string;
  kindSchemaVersion: number;
  occurredAtUtc: string;
  sourceOrigin: string;
  /** Agent-serialized DecisionSignal JSON — opaque blob, decoded lazily for the Evidence-Drawer. */
  payloadJson: string;
}

export interface SignalsResponse {
  success: boolean;
  sessionId: string;
  count: number;
  truncated: boolean;
  signals: SignalRecord[];
}
