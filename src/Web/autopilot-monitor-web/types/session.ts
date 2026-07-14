export interface Session {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  completedAt?: string;
  status: string;
  currentPhase: number;
  eventCount: number;
  durationSeconds: number;
  /** System reboots observed during enrollment (V2 only — 0 for V1 and sessions before this feature). */
  rebootCount?: number;
  failureReason?: string;
  /** Origin of a Failed status — "" for agent-reported, "rule:<RuleId>" for rule-based, "manual" for portal. */
  failureSource?: string;
  /** Non-empty only when the BACKEND declared this session Succeeded (timeout-sweep reconcile or late-completion upgrade) — carries the justification so operators can tell it apart from an agent-reported success. */
  reconcileReason?: string;
  /** Non-null only when an administrator flipped the session manually via the portal. Values: "Succeeded" | "Failed". */
  adminMarkedAction?: string;
  enrollmentType?: string; // "v1" | "v2" — absent for sessions before this feature
  diagnosticsBlobName?: string;
  lastEventAt?: string;
  /** Set when the maintenance sweep first observed the agent had gone silent (2h+ no events). Surfaced in the reconcile banner so the silence window is transparent. */
  stalledAt?: string;
  isPreProvisioned?: boolean;
  isHybridJoin?: boolean;
  isUserDriven?: boolean;
  /** Self-deploying/kiosk Autopilot profile (CloudAssignedOobeConfig 0x20|0x40, agent-detected at registration). */
  isSelfDeployingProfile?: boolean;
  agentVersion?: string;
  // OS details
  osName?: string;
  osBuild?: string;
  osDisplayVersion?: string;
  osEdition?: string;
  osLanguage?: string;
  // Geographic location
  geoCountry?: string;
  geoRegion?: string;
  geoCity?: string;
  /**
   * Compact JSON snapshot of "last known session state" written by the maintenance
   * 5h-timeout sweep when a session graduates to terminal Failed (Hybrid User-Driven
   * completion-gap fix, 2026-05-01). Empty for healthy completions and sessions that
   * predate the field. The detail page renders a collapsible "Failure Snapshot" block
   * when populated.
   */
  failureSnapshotJson?: string;
}
