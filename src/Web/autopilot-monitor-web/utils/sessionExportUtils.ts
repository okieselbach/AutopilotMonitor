/**
 * Shared session export utilities.
 * Used by both the admin-config SessionExportSection and the session report modal.
 */

export interface SessionExportEvent {
  eventId: string;
  sessionId: string;
  tenantId: string;
  timestamp: string;
  eventType: string;
  severity: string;
  source: string;
  phase: number;
  phaseName?: string;
  message: string;
  sequence: number;
  rowKey?: string;
  receivedAt?: string;
  data?: Record<string, unknown>;
}

export const EXPORT_V1_PHASE_NAMES: Record<number, string> = {
  0: "Start", 1: "Device Preparation", 2: "Device Setup",
  3: "Apps (Device)", 4: "Account Setup", 5: "Apps (User)",
  6: "Finalizing Setup", 7: "Complete", 99: "Failed"
};
export const EXPORT_V2_PHASE_NAMES: Record<number, string> = {
  0: "Start", 1: "Device Preparation", 2: "Device Setup",
  3: "App Installation", 4: "Account Setup", 5: "Apps (User)",
  6: "Finalizing Setup", 7: "Complete", 99: "Failed"
};
export const EXPORT_V1_PHASE_ORDER = ["Start", "Device Preparation", "Device Setup",
  "Apps (Device)", "Account Setup", "Apps (User)", "Finalizing Setup", "Complete", "Failed"];
export const EXPORT_V2_PHASE_ORDER = ["Start", "Device Preparation", "App Installation",
  "Finalizing Setup", "Complete", "Failed"];

// Exported for the shared-manifest parity test — must match the C# EventSeverity enum.
export const SEVERITY_INT: Record<string, number> = {
  Trace: -1, Debug: 0, Info: 1, Warning: 2, Error: 3, Critical: 4
};

/**
 * Quote a CSV cell and make untrusted content inert for spreadsheet consumers (CWE-1236).
 * Excel/LibreOffice unquote the cell and then evaluate a leading `=`, `+`, `-`, `@`
 * (and tab/CR-led cells) as a formula — device-originated strings (event message,
 * device name, serial, failure reason, ...) must never reach a cell in that shape.
 * A leading apostrophe forces text interpretation; embedded quotes are doubled.
 */
export function csvCell(v: string | number | boolean | undefined | null): string {
  const raw = String(v ?? "");
  const neutralized = /^[=+\-@\t\r]/.test(raw) ? `'${raw}` : raw;
  return `"${neutralized.replace(/"/g, '""')}"`;
}

export function generateCsvExport(events: SessionExportEvent[]) {
  const isV1 = events.some(e => e.phase === 2);
  const phaseNames = isV1 ? EXPORT_V1_PHASE_NAMES : EXPORT_V2_PHASE_NAMES;
  // Sort exactly as Azure Table Storage: by timestamp ascending, then sequence ascending
  const sorted = [...events].sort((a, b) => {
    const tCmp = (a.timestamp ?? "").localeCompare(b.timestamp ?? "");
    if (tCmp !== 0) return tCmp;
    return (a.sequence ?? 0) - (b.sequence ?? 0);
  });
  // Columns match the Events Azure Table Storage schema exactly
  // PartitionKey = TenantId_SessionId, RowKey = Timestamp_Sequence
  // PhaseName is a derived extra column appended after DataJson for convenience
  const header = "PartitionKey,RowKey,EventId,SessionId,TenantId,Timestamp,ReceivedAt,EventType,Severity,Source,Phase,Message,Sequence,DataJson,PhaseName";
  const rows = sorted.map(e => {
    const sev = e.severity ?? "";
    const sevInt = SEVERITY_INT[sev] ?? SEVERITY_INT[sev.charAt(0).toUpperCase() + sev.slice(1).toLowerCase()];
    const severityCell = sevInt !== undefined ? `${sev} (${sevInt})` : sev;
    return [
      csvCell(`${e.tenantId ?? ""}_${e.sessionId ?? ""}`),
      csvCell(e.rowKey ?? ""),
      csvCell(e.eventId ?? ""),
      csvCell(e.sessionId ?? ""),
      csvCell(e.tenantId ?? ""),
      csvCell(e.timestamp ?? ""),
      csvCell(e.receivedAt ?? ""),
      csvCell(e.eventType ?? ""),
      csvCell(severityCell),
      csvCell(e.source ?? ""),
      String(e.phase ?? 0),
      csvCell(e.message ?? ""),
      String(e.sequence ?? 0),
      csvCell(e.data ? JSON.stringify(e.data) : ""),
      csvCell(phaseNames[e.phase] ?? "Unknown"),
    ].join(",");
  });
  return "\uFEFF" + header + "\n" + rows.join("\n");
}

export interface SessionCsvData {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  osName?: string;
  osBuild?: string;
  osDisplayVersion?: string;
  osEdition?: string;
  osLanguage?: string;
  isUserDriven?: boolean;
  isPreProvisioned?: boolean;
  startedAt: string;
  completedAt?: string;
  agentVersion?: string;
  enrollmentType?: string;
  currentPhase: number;
  status: string;
  eventCount: number;
  failureReason?: string;
  lastEventAt?: string;
  durationSeconds: number;
  diagnosticsBlobName?: string;
  rebootCount?: number;
  failureSource?: string;
  reconcileReason?: string;
  adminMarkedAction?: string;
  validatedBy?: string;
  isHybridJoin?: boolean;
  isSelfDeployingProfile?: boolean;
  connectionType?: string;
  geoCountry?: string;
  geoRegion?: string;
  geoCity?: string;
  avgApiLatencyMs?: number;
  apiRequestCount?: number;
  stalledAt?: string;
  failureSnapshotJson?: string;
  isCloudPc?: boolean;
}

export function generateSessionCsvExport(session: SessionCsvData): string {
  // Columns mirror the Sessions Azure Table Storage schema
  // PartitionKey = TenantId, RowKey = SessionId
  // Newer fields are APPENDED (never inserted) so the column prefix stays stable
  // for anything parsing older exports.
  const header = "PartitionKey,RowKey,SerialNumber,DeviceName,Manufacturer,Model,OsName,OsBuild,OsDisplayVersion,OsEdition,OsLanguage,IsUserDriven,IsPreProvisioned,StartedAt,CompletedAt,AgentVersion,EnrollmentType,CurrentPhase,Status,EventCount,FailureReason,LastEventAt,DurationSeconds,DiagnosticsBlobName,RebootCount,FailureSource,ReconcileReason,AdminMarkedAction,ValidatedBy,IsHybridJoin,IsSelfDeployingProfile,ConnectionType,GeoCountry,GeoRegion,GeoCity,AvgApiLatencyMs,ApiRequestCount,StalledAt,FailureSnapshotJson,IsCloudPc";
  const row = [
    csvCell(session.tenantId),
    csvCell(session.sessionId),
    csvCell(session.serialNumber),
    csvCell(session.deviceName),
    csvCell(session.manufacturer),
    csvCell(session.model),
    csvCell(session.osName),
    csvCell(session.osBuild),
    csvCell(session.osDisplayVersion),
    csvCell(session.osEdition),
    csvCell(session.osLanguage),
    String(session.isUserDriven ?? ""),
    String(session.isPreProvisioned ?? ""),
    csvCell(session.startedAt),
    csvCell(session.completedAt),
    csvCell(session.agentVersion),
    csvCell(session.enrollmentType),
    String(session.currentPhase ?? ""),
    csvCell(session.status),
    String(session.eventCount ?? ""),
    csvCell(session.failureReason),
    csvCell(session.lastEventAt),
    String(session.durationSeconds ?? ""),
    csvCell(session.diagnosticsBlobName),
    String(session.rebootCount ?? ""),
    csvCell(session.failureSource),
    csvCell(session.reconcileReason),
    csvCell(session.adminMarkedAction),
    csvCell(session.validatedBy),
    String(session.isHybridJoin ?? ""),
    String(session.isSelfDeployingProfile ?? ""),
    csvCell(session.connectionType),
    csvCell(session.geoCountry),
    csvCell(session.geoRegion),
    csvCell(session.geoCity),
    String(session.avgApiLatencyMs ?? ""),
    String(session.apiRequestCount ?? ""),
    csvCell(session.stalledAt),
    csvCell(session.failureSnapshotJson),
    String(session.isCloudPc ?? ""),
  ].join(",");
  return "\uFEFF" + header + "\n" + row;
}

export interface RuleResultCsvData {
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
}

export function generateRuleResultsCsvExport(results: RuleResultCsvData[]): string {
  // Columns match the RuleResults Azure Table Storage schema exactly
  // PartitionKey = TenantId_SessionId, RowKey = RuleId
  const header = "PartitionKey,RowKey,ResultId,SessionId,TenantId,RuleId,RuleTitle,Severity,Category,ConfidenceScore,Explanation,RemediationJson,RelatedDocsJson,MatchedConditionsJson,DetectedAt";
  const rows = results.map(r => [
    csvCell(`${r.tenantId}_${r.sessionId}`),
    csvCell(r.ruleId),
    csvCell(r.resultId),
    csvCell(r.sessionId),
    csvCell(r.tenantId),
    csvCell(r.ruleId),
    csvCell(r.ruleTitle),
    csvCell(r.severity),
    csvCell(r.category),
    String(r.confidenceScore ?? ""),
    csvCell(r.explanation),
    csvCell(r.remediation ? JSON.stringify(r.remediation) : ""),
    csvCell(r.relatedDocs ? JSON.stringify(r.relatedDocs) : ""),
    csvCell(r.matchedConditions ? JSON.stringify(r.matchedConditions) : ""),
    csvCell(r.detectedAt),
  ].join(","));
  return "\uFEFF" + header + "\n" + rows.join("\n");
}

function groupEventsByPhase(
  eventsToGroup: SessionExportEvent[],
  phaseNames: Record<number, string>,
  phaseOrder: string[],
  options?: { preventPhaseRegression?: boolean }
): Record<string, SessionExportEvent[]> {
  const preventRegression = options?.preventPhaseRegression === true;
  const grouped: Record<string, SessionExportEvent[]> = {};
  phaseOrder.forEach(p => { grouped[p] = []; });
  let lastNamedPhase = phaseOrder[0];
  let maxPhaseIndex = 0;
  for (const ev of eventsToGroup) {
    const name = phaseNames[ev.phase];
    if (name && name !== "Unknown") {
      if (preventRegression) {
        const candidateIndex = phaseOrder.indexOf(name);
        if (candidateIndex >= 0 && candidateIndex >= maxPhaseIndex) {
          lastNamedPhase = name;
          maxPhaseIndex = candidateIndex;
          if (grouped[name]) grouped[name].push({ ...ev, phaseName: name });
        } else {
          // Phase would regress (e.g. reboot agent_started) — keep current phase
          if (grouped[lastNamedPhase]) grouped[lastNamedPhase].push({ ...ev, phaseName: name });
        }
      } else {
        lastNamedPhase = name;
        if (grouped[name]) grouped[name].push({ ...ev, phaseName: name });
      }
    } else {
      if (grouped[lastNamedPhase]) grouped[lastNamedPhase].push({ ...ev, phaseName: "Unknown" });
    }
  }
  return grouped;
}

function renderPhaseBlocks(
  lines: string[],
  grouped: Record<string, SessionExportEvent[]>,
  phaseOrder: string[],
  severityLabel: (s: string) => string
) {
  for (const phase of phaseOrder) {
    const phaseEvents = grouped[phase] ?? [];
    lines.push("");
    lines.push("\u2550".repeat(43));
    lines.push(`  ${phase}  (${phaseEvents.length} event${phaseEvents.length !== 1 ? "s" : ""})`);
    lines.push("\u2550".repeat(43));
    if (phaseEvents.length === 0) {
      lines.push("  (no events)");
    } else {
      for (const ev of phaseEvents) {
        const ts = ev.timestamp ? new Date(ev.timestamp).toISOString().replace("T", " ").substring(0, 23) : "?";
        lines.push(`[${ts}] [${severityLabel(ev.severity)}] ${ev.eventType} \u2014 ${ev.message}`);
        let detail = `  Source: ${ev.source ?? "?"} | Seq: ${ev.sequence ?? "?"} | EventId: ${ev.eventId ?? "?"}`;
        if (ev.phaseName === "Unknown") detail += ` | RawPhase: ${ev.phase}`;
        lines.push(detail);
        if (ev.data && Object.keys(ev.data).length > 0) {
          lines.push(`  Data: ${JSON.stringify(ev.data)}`);
        }
      }
    }
  }
}

export function generateUiExport(
  events: SessionExportEvent[],
  sessionId: string,
  tenantId: string,
  sessionStatus?: string
) {
  const isV1 = events.some(e => e.phase === 2);
  const phaseNames = isV1 ? EXPORT_V1_PHASE_NAMES : EXPORT_V2_PHASE_NAMES;
  const phaseOrder = isV1 ? EXPORT_V1_PHASE_ORDER : EXPORT_V2_PHASE_ORDER;

  const sorted = [...events].sort((a, b) => a.sequence - b.sequence);

  // Detect WhiteGlove / Pre-Provisioning session (mirrors UI logic)
  const isWhiteGlove = sorted.some(e => e.eventType === "whiteglove_complete");

  const pad = (s: string, len: number) => s.padEnd(len);
  const severityLabel = (s: string) => pad(s ?? "Unknown", 7);

  const lines: string[] = [];
  lines.push("AUTOPILOT MONITOR \u2014 SESSION EVENT EXPORT");
  lines.push("=========================================");
  lines.push(`Session ID   : ${sessionId}`);
  lines.push(`Tenant ID    : ${tenantId}`);
  lines.push(`Exported at  : ${new Date().toISOString()}`);
  lines.push(`Total events : ${events.length}`);
  lines.push(`Enrollment   : ${isV1 ? "V1" : "V2"}`);
  if (isWhiteGlove) {
    lines.push(`Session type : WhiteGlove (Pre-Provisioning)`);
  }

  if (!isWhiteGlove) {
    // Standard single timeline
    const grouped = groupEventsByPhase(sorted, phaseNames, phaseOrder, { preventPhaseRegression: true });
    renderPhaseBlocks(lines, grouped, phaseOrder, severityLabel);
  } else {
    // WhiteGlove session: two-part timeline, matching the UI split logic.
    //
    // UI TIMELINE BEHAVIOUR (reproduced here):
    //   The session page shows two separate timelines for WhiteGlove sessions:
    //
    //   ┌──────────────────────────────────────────┐
    //   │  Pre-Provisioning Part  [WhiteGlove]     │  ← amber badge
    //   │  (phases up to and including the         │
    //   │   "whiteglove_complete" event)            │
    //   └──────────────────────────────────────────┘
    //
    //   followed by one of two states:
    //
    //   A) User Enrollment Part [Resumed]           ← blue badge
    //      (phases from the next agent_started boot
    //       after whiteglove_complete)
    //
    //   B) *** AWAITING USER ENROLLMENT ***
    //      "Pre-provisioning is complete. The
    //       timeline will continue when the user
    //       powers on the device."
    //      (shown when no user-enrollment events
    //       exist and session status is "Pending")
    //
    // SPLIT POINT CALCULATION (mirrors useSessionDerivedData.ts via the agent-side
    // V1-symmetric resume mechanic, PR-A):
    //   Primary:  `whiteglove_resumed` is the definitive Part 2 marker (emitted by the
    //             orchestrator after Archive-and-Reset detects WhiteGloveSealed snapshot)
    //               → split = that event's sequence - 1
    //   Fallback: older agents — first agent_started AFTER whiteglove_complete is Part 2 boot
    //               → split = that event's sequence - 1
    //   Single:   only pre-prov part present
    //               → split = whiteglove_complete.sequence
    //
    //   Events are assigned to parts purely by sequence number (no whiteglove_complete override).

    const wgCompleteEv = sorted.find(e => e.eventType === "whiteglove_complete");
    const wgSeq = wgCompleteEv?.sequence ?? sorted[sorted.length - 1]?.sequence ?? 0;
    const resumedEv = sorted.find(e => e.eventType === "whiteglove_resumed");

    let splitSeq: number;
    if (resumedEv) {
      splitSeq = resumedEv.sequence - 1;
    } else {
      const afterWg = sorted.filter(e => e.eventType === "agent_started" && e.sequence > wgSeq);
      splitSeq = afterWg.length > 0 ? afterWg[0].sequence - 1 : wgSeq;
    }

    // Events are assigned purely by sequence number — no special-casing for whiteglove_complete.
    // Preserves chronological order in both parts.
    const preProvEvents = sorted.filter(e => e.sequence <= splitSeq);
    // Sort Part 2 events by timestamp (primary) then sequence (secondary) to handle
    // potential duplicate sequences from agent sequence counter not persisted before reboot.
    const userEnrollEvents = sorted.filter(e => e.sequence > splitSeq).sort((a, b) => {
      const tCmp = (a.timestamp ?? "").localeCompare(b.timestamp ?? "");
      if (tCmp !== 0) return tCmp;
      return (a.sequence ?? 0) - (b.sequence ?? 0);
    });

    // --- Pre-Provisioning Part ---
    lines.push("");
    lines.push("#".repeat(43));
    lines.push(`  PRE-PROVISIONING PART  [WhiteGlove]`);
    lines.push("#".repeat(43));
    const preProvGrouped = groupEventsByPhase(preProvEvents, phaseNames, phaseOrder, { preventPhaseRegression: true });
    renderPhaseBlocks(lines, preProvGrouped, phaseOrder, severityLabel);

    // --- User Enrollment Part or Awaiting Banner ---
    lines.push("");
    if (userEnrollEvents.length > 0) {
      lines.push("#".repeat(43));
      lines.push(`  USER ENROLLMENT PART  [Resumed]`);
      lines.push("#".repeat(43));
      const userEnrollGrouped = groupEventsByPhase(userEnrollEvents, phaseNames, phaseOrder, { preventPhaseRegression: true });
      renderPhaseBlocks(lines, userEnrollGrouped, phaseOrder, severityLabel);
    } else {
      // Mirrors the "Awaiting User Enrollment" banner shown in the UI when
      // there are no user-enrollment events and the session is still Pending.
      lines.push("*** AWAITING USER ENROLLMENT ***");
      lines.push("Pre-provisioning is complete. The timeline will continue when the user powers on the device.");
      if (sessionStatus) lines.push(`Session status: ${sessionStatus}`);
    }
  }

  return lines.join("\n");
}
