import { McpServer } from '@modelcontextprotocol/server';
import { z } from 'zod';

/**
 * MCP-protocol prompts: reusable, parameterized diagnostic workflows.
 *
 * Prompts are surfaced by the host as slash-commands / templates the user can
 * invoke directly. Each one seeds the conversation with a precise tool-call
 * plan, so the model does not have to rediscover the TIER-1/2/3 search order or
 * the "summary first, then drill" pattern on every investigation. They are
 * read-only — every step they instruct maps to a read-only tool.
 *
 * Note: MCP prompt arguments are always strings on the wire. We validate shape
 * with Zod but keep types as strings; numeric/optional semantics are described
 * in the argument text for the model.
 */
export function registerPrompts(server: McpServer, ga: boolean): void {
  server.registerPrompt(
    'investigate-failed-session',
    {
      title: 'Investigate Failed Session',
      description:
        'Guided root-cause investigation of a single enrollment session. Seeds the ' +
        'summary-first → drill-down workflow and asks for a structured verdict.',
      argsSchema: { sessionId: z.string().describe('Session UUID to investigate') },
    },
    ({ sessionId }) => ({
      messages: [
        {
          role: 'user',
          content: {
            type: 'text',
            text:
              `Investigate enrollment session ${sessionId} and explain why it ended the way it did.\n\n` +
              'Follow this order:\n' +
              `1. Call get_session_summary(sessionId="${sessionId}") first — it gives status, the ` +
              'noise-filtered key-event timeline, aggregate stats, and any rule analysis in one shot.\n' +
              '2. If the summary shows errors or a failure, escalate: use search_events ' +
              '(hybrid; depth="deep" if the fast pass is thin) for the failing area, then get_session_events for the full ' +
              'chronological stream around the first error. Follow nextLink until the relevant window is covered.\n' +
              '3. Cross-check the rule analysis (it carries probable cause + remediation). If the summary ' +
              'reported keyEventsTruncated, pull the raw events rather than trusting the capped list.\n\n' +
              'Then report: (a) final outcome and phase reached, (b) the single most likely root cause with ' +
              'the event evidence that supports it, (c) concrete remediation steps, (d) confidence level.',
          },
        },
      ],
    }),
  );

  server.registerPrompt(
    'debug-session',
    {
      title: 'Debug Session (Backend + Agent Diagnostics)',
      description:
        'Deep end-to-end session debug: correlate backend telemetry with the on-device agent ' +
        'diagnostics ZIP (downloaded + analyzed locally). The high-leverage workflow for "why did ' +
        'this enrollment go wrong". Requires a client with local file/shell tools (e.g. Claude Code).',
      argsSchema: {
        sessionId: z.string().describe('Session UUID to debug'),
        question: z.string().optional().describe('Optional specific question, e.g. "why is it failed?"'),
        tenantId: z
          .string()
          .optional()
          .describe(ga ? 'Optional tenant ID. Omit to auto-resolve from the session (Global Admin).' : 'Optional tenant ID. Defaults to your tenant.'),
      },
    },
    ({ sessionId, question, tenantId }) => ({
      messages: [
        {
          role: 'user',
          content: {
            type: 'text',
            text:
              `Debug enrollment session ${sessionId}` +
              (tenantId ? ` (tenant ${tenantId})` : '') +
              (question ? `. Focus: ${question}` : '') +
              '.\n\n' +
              'Do a correlated client↔backend analysis:\n' +
              `1. Call get_session_summary(sessionId="${sessionId}"${tenantId ? `, tenantId="${tenantId}"` : ''}) ` +
              'first — status, noise-filtered timeline, stats, rule analysis in one shot.\n' +
              `2. Call get_session_diagnostics(sessionId="${sessionId}"${tenantId ? `, tenantId="${tenantId}"` : ''}). ` +
              'If available=true, DOWNLOAD the ZIP from downloadUrl using your local tools (no auth header — ' +
              'it is a short-lived signed ticket), unzip it locally, and read files per the returned zipMap: ' +
              'start with AgentState/final-status.json and the agent log (grep [ERROR]/[WARN] first), then ' +
              'journal/signal logs. AppWorkload*.log can be hundreds of MB → grep only, never read whole. ' +
              'If available=false, note it and continue with backend data only.\n' +
              '3. Build a correlated timeline merging the agent log (client truth) with the backend Events. ' +
              'Use get_session_events / query_raw_events for the raw stream around the first error; gaps ' +
              'between agent log and Events reveal upload/network issues. Use search_knowledge to look up ' +
              'rules / IME patterns / error codes you encounter.\n' +
              (ga
                ? '4. If the problem looks like a backend ingest/upload issue (events the agent logged as sent ' +
                  'are missing, or status disagrees with events), use query_backend_logs (App Insights KQL) and ' +
                  'query_table (e.g. RuleResults, AppInstallSummaries) to trace it.\n'
                : '') +
              '\nThen report: (a) final outcome + phase reached, (b) the single most likely root cause with ' +
              'the specific evidence (agent-log line AND/OR backend event), (c) concrete remediation, ' +
              '(d) confidence level. Cite both client and backend sources where they corroborate.',
          },
        },
      ],
    }),
  );

  server.registerPrompt(
    'cve-exposure-audit',
    {
      title: 'CVE Exposure Audit',
      description:
        'Fleet exposure audit for a specific CVE: which devices/sessions are affected, ' +
        'how severe, and what to do about it.',
      argsSchema: {
        cveId: z.string().describe('CVE identifier, e.g. "CVE-2024-21447"'),
        tenantId: z
          .string()
          .optional()
          .describe(ga ? 'Optional tenant ID to scope the audit. Omit for a cross-tenant audit (Global Admin).' : 'Optional tenant ID. Defaults to your tenant.'),
      },
    },
    ({ cveId, tenantId }) => ({
      messages: [
        {
          role: 'user',
          content: {
            type: 'text',
            text:
              `Audit fleet exposure to ${cveId}` +
              (tenantId ? ` within tenant ${tenantId}` : (ga ? ' across all tenants' : ' in your tenant')) +
              '.\n\n' +
              '1. Call search_sessions_by_cve with the cveId' +
              (tenantId ? ' and tenantId' : '') +
              '. Use pageSize=1000 and follow nextLink until it is absent — exposure audits must be complete, not sampled.\n' +
              '2. Tally affected sessions/devices, and break them down by overallRisk and CVSS score.\n' +
              '3. Use search_knowledge to look up remediation guidance for the affected software if a relevant rule exists.\n\n' +
              'Report: total affected devices, severity breakdown, the most-exposed manufacturers/models if a pattern ' +
              'stands out, and prioritized remediation. State explicitly if vulnerability scanning is disabled (empty result ≠ "not affected").',
          },
        },
      ],
    }),
  );

  server.registerPrompt(
    'author-rule',
    {
      title: 'Author a Gather / Analyze Rule',
      description:
        'Guided creation of a custom gather or analyze rule from a scenario description, ' +
        'with validation and a session dry-run before anything is deployed.',
      argsSchema: {
        scenario: z.string().describe('What should be detected or collected, in plain language'),
        sessionId: z.string().optional().describe('Optional: a recent session UUID to dry-run the draft analyze rule against'),
      },
    },
    ({ scenario, sessionId }) => ({
      messages: [
        {
          role: 'user',
          content: {
            type: 'text',
            text:
              `Create an Autopilot Monitor rule for this scenario: ${scenario}\n\n` +
              'Work through this order:\n' +
              '1. Read get_resource(name="rule_authoring_guide") plus get_resource(name="rule_schemas") ' +
              'and get_resource(name="event_types"). For gather rules also get_resource(name="rule_guardrails").\n' +
              '2. Decide: does this need a gather rule (new data from the device), an analyze rule ' +
              '(evaluate existing events), or both? If both, draft the gather rule first and reference ' +
              'its outputEventType from the analyze rule.\n' +
              '3. Draft the rule JSON and call validate_rule with it. Fix every error; explain warnings you keep.\n' +
              (sessionId
                ? `4. Dry-run the analyze rule: test_analyze_rule(sessionId="${sessionId}", rule=<draft>). ` +
                  'Walk through the per-condition trace; iterate until the verdict matches the expectation, and check the interpolated explanation reads well.\n'
                : '4. Ask me for a recent sessionId (one where the rule SHOULD fire and ideally one where it should not) and dry-run the analyze rule with test_analyze_rule.\n') +
              '5. Present the final rule JSON with a short explanation of when it fires, its confidence ' +
              'model, and how to create it in the portal (Settings → Gather Rules / Analyze Rules).',
          },
        },
      ],
    }),
  );

  // Global Admin only — relies on get_platform_metrics (a GA-only tool that is
  // not registered for normal users). Hidden from non-GA so it never references
  // a tool they cannot see.
  if (ga) server.registerPrompt(
    'compare-agent-versions',
    {
      title: 'Compare Agent Versions',
      description:
        'Compare enrollment success rate and agent resource usage across Monitor Agent ' +
        'versions over a time window — useful for validating a rollout.',
      argsSchema: { days: z.string().optional().describe('Time window in days (1-365). Defaults to 30 if omitted.') },
    },
    ({ days }) => {
      const window = days ?? '30';
      return {
        messages: [
          {
            role: 'user',
            content: {
              type: 'text',
              text:
                `Compare Monitor Agent versions over the last ${window} days and tell me whether the newest build is healthy.\n\n` +
                `1. Call get_platform_metrics(days=${window}) for the per-agent-version CPU/memory/network breakdown.\n` +
                '2. For success-rate-by-version, use query_raw_sessions with a lean projection ' +
                '(fields="Status,AgentVersion,StartedAt" — raw rows use the literal PascalCase column names). ' +
                'Sweep each version line with agentVersionPrefix ' +
                '(e.g. "2.0.") rather than one call per build, and follow nextLink for full counts.\n' +
                `3. Optionally call get_metrics(days=${window}) for the overall failure-rate baseline to compare against.\n\n` +
                'Report a per-version table: session count, success rate, avg CPU, avg working set. Flag any version ' +
                'whose success rate or resource profile is a clear regression versus its predecessor.',
            },
          },
        ],
      };
    },
  );
}
