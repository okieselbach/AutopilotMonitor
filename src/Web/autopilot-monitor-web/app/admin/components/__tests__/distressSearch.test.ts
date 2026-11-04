import { describe, it, expect } from "vitest";
import { matchesDistressSearch } from "../distressSearch";

const report = {
  tenantId: "11111111-2222-3333-4444-555555555555",
  errorType: "HardwareNotAllowed",
  manufacturer: "LENOVO",
  model: "21HD003XGE",
  serialNumber: "PF0TEST1",
  agentVersion: "2.0.1500+abc",
  osBuild: "26100.4351",
  message: "Backend returned 403 during agent/config",
  sourceIp: "203.0.113.7",
  certThumbprint: "A".repeat(40),
  certSubject: "CN=device-test",
};

describe("distress search", () => {
  it("matches the serial number by substring, case-insensitively and trimmed", () => {
    expect(matchesDistressSearch(report, "PF0TEST1")).toBe(true);
    expect(matchesDistressSearch(report, "  pf0te ")).toBe(true);
    expect(matchesDistressSearch(report, "PF9NOHIT")).toBe(false);
  });

  it("matches tenant id, hardware, failure, certificate and source address", () => {
    expect(matchesDistressSearch(report, "2222-3333")).toBe(true);
    expect(matchesDistressSearch(report, "lenovo")).toBe(true);
    expect(matchesDistressSearch(report, "21HD")).toBe(true);
    expect(matchesDistressSearch(report, "hardwarenot")).toBe(true);
    expect(matchesDistressSearch(report, "2.0.1500")).toBe(true);
    expect(matchesDistressSearch(report, "26100")).toBe(true);
    expect(matchesDistressSearch(report, "agent/config")).toBe(true);
    expect(matchesDistressSearch(report, "203.0.113")).toBe(true);
    expect(matchesDistressSearch(report, "aaaa")).toBe(true);
    expect(matchesDistressSearch(report, "device-test")).toBe(true);
  });

  it("matches the badge label the list shows for the error type", () => {
    expect(matchesDistressSearch(report, "hw blocked", "HW Blocked")).toBe(true);
    expect(matchesDistressSearch(report, "hw blocked")).toBe(false);
  });

  it("skips fields the agent did not send", () => {
    const sparse = { tenantId: report.tenantId, errorType: "DeviceNotRegistered" };
    expect(matchesDistressSearch(sparse, "lenovo")).toBe(false);
    expect(matchesDistressSearch(sparse, "notregistered")).toBe(true);
  });

  it("matches everything for an empty or blank query", () => {
    expect(matchesDistressSearch(report, "")).toBe(true);
    expect(matchesDistressSearch(report, "   ")).toBe(true);
  });
});
