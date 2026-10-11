import { describe, it, expect } from "vitest";
import { buildInvestigationPrompt } from "../investigationPrompt";

const SESSION = "3f9c2d71-5b8e-4c0a-9e14-6d2b7a815c40";
const TENANT = "11111111-2222-4333-8444-555555555555";

describe("buildInvestigationPrompt", () => {
  it("names the session and walks summary → events → diagnostics → report → rule", () => {
    const prompt = buildInvestigationPrompt({ sessionId: SESSION, canDryRun: true });
    expect(prompt).toContain(`session ${SESSION} with the Autopilot Monitor MCP tools`);
    const order = ["get_session_summary", "search_events", "get_session_diagnostics", "4. Report", "validate_rule"]
      .map(marker => prompt.indexOf(marker));
    expect(order.every(i => i >= 0)).toBe(true);
    expect([...order].sort((a, b) => a - b)).toEqual(order);
    expect(prompt).toContain("never read whole");
  });

  it("adds the tenant only for a cross-tenant session", () => {
    expect(buildInvestigationPrompt({ sessionId: SESSION, canDryRun: true })).not.toContain("(tenant ");
    expect(buildInvestigationPrompt({ sessionId: SESSION, tenantId: TENANT, canDryRun: true }))
      .toContain(`session ${SESSION} (tenant ${TENANT})`);
  });

  it.each([
    { canDryRun: true, expected: "dry-run it with test_analyze_rule", absent: "so a Tenant Admin" },
    { canDryRun: false, expected: "so a Tenant Admin can dry-run it", absent: "dry-run it with test_analyze_rule" },
  ])("canDryRun=$canDryRun names only the tools the caller has", ({ canDryRun, expected, absent }) => {
    const prompt = buildInvestigationPrompt({ sessionId: SESSION, canDryRun });
    expect(prompt).toContain(expected);
    expect(prompt).not.toContain(absent);
  });
});
