/**
 * The copyable deep-analysis prompt offered on a failed session that no analyze rule explains. It
 * spells out the whole investigation, so it works in every MCP client — also those that offer no MCP
 * prompts (where the built-in `debug-session` prompt does the same). Tool names are the MCP server's;
 * the dry-run step is written only for callers whose MCP catalog lists test_analyze_rule.
 */
export interface InvestigationPromptInput {
  sessionId: string;
  /** Set when the session belongs to another tenant than the viewer's (Global Admin / MSP drill-in). */
  tenantId?: string;
  /** Tenant Admin or platform role: test_analyze_rule is available to them. */
  canDryRun: boolean;
}

export function buildInvestigationPrompt({ sessionId, tenantId, canDryRun }: InvestigationPromptInput): string {
  const target = tenantId ? `session ${sessionId} (tenant ${tenantId})` : `session ${sessionId}`;
  const ruleStep = canDryRun
    ? "Then dry-run it with test_analyze_rule against this session (it should fire) and against a successful session (it should stay silent), and give me the final JSON."
    : "Give me the validated JSON so a Tenant Admin can dry-run it and create it in the portal.";
  return [
    `Investigate Autopilot Monitor enrollment ${target} with the Autopilot Monitor MCP tools. No analyze rule flagged this failure, so the cause may be new.`,
    "",
    "1. Call get_session_summary first. Read coverage.gaps before you treat a missing event as evidence.",
    "2. Find the first error and what led to it: search_events for the failing area, then get_session_events for the full stream around it. Decode every error code with lookup_error_code and check search_knowledge for rules or IME log patterns that name it.",
    "3. Call get_session_diagnostics. If a package is available and you can work with files, download and unzip it, then grep (never read whole) ImeLogs/AppWorkload*.log, ImeLogs/IntuneManagementExtension.log and AgentLogs/agent.log around the first error. Without a package, continue with the session events.",
    "4. Report the final outcome and phase, the most likely root cause with the exact evidence (event or log line), concrete remediation, and your confidence.",
    `5. If the cause could happen again, draft an analyze rule that would flag it: read get_resource(name="rule_authoring_guide") and check the draft with validate_rule. ${ruleStep}`,
  ].join("\n");
}
