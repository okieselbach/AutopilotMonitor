/**
 * Shared test factories for wire-shaped fixtures.
 *
 * The wire types (utils/wire-types.generated.ts) carry the exact C# optionality —
 * plain-`bool`/`int` fields are required, so hand-built object literals in tests broke
 * on every contract change. Build fixtures through these factories instead and only
 * spell out the fields the test is actually about.
 *
 * Object.assign (not object spread) applies the overrides: spreading a Partial<T>
 * re-widens every property to `T | undefined`, while Object.assign keeps the full
 * wire type AND still lets a test force `data: undefined` at runtime to probe
 * legacy-payload tolerance.
 */
import type { EnrollmentEvent, Session } from "@/types";

export function makeSession(overrides: Partial<Session> = {}): Session {
  return Object.assign(
    {
      sessionId: "session-1",
      tenantId: "tenant-1",
      serialNumber: "SN-0001",
      deviceName: "TEST-DEVICE",
      manufacturer: "",
      model: "",
      startedAt: "2026-08-28T10:00:00Z",
      currentPhase: 0,
      currentPhaseDetail: "",
      status: "InProgress" as const,
      failureReason: "",
      failureSource: "",
      reconcileReason: "",
      espSoftFailure: false,
      completionSource: "",
      validatedBy: "",
      eventCount: 0,
      enrollmentType: "v1",
      diagnosticsBlobName: "",
      isPreProvisioned: false,
      isHybridJoin: false,
      isSelfDeployingProfile: false,
      isCloudPc: false,
      osName: "",
      osBuild: "",
      osDisplayVersion: "",
      osEdition: "",
      osLanguage: "",
      isUserDriven: true,
      agentVersion: "",
      imeAgentVersion: "",
      geoCountry: "",
      geoRegion: "",
      geoCity: "",
      geoLoc: "",
      platformScriptCount: 0,
      remediationScriptCount: 0,
      rebootCount: 0,
      excessiveEventsAlerted: false,
      excessiveEventsAutoActioned: false,
      pendingActionsJson: "",
      failureSnapshotJson: "",
      deletionState: "",
    },
    overrides,
  );
}

export function makeEvent(overrides: Partial<EnrollmentEvent> = {}): EnrollmentEvent {
  return Object.assign(
    {
      eventId: "evt-1",
      sessionId: "session-1",
      timestamp: "2026-08-28T10:00:00Z",
      eventType: "info_event",
      severity: "Info",
      source: "Test",
      phase: 0,
      phaseName: "",
      message: "",
      data: {} as Record<string, unknown>,
      sequence: 1,
      timestampClamped: false,
      rowKey: "",
    },
    overrides,
  );
}
