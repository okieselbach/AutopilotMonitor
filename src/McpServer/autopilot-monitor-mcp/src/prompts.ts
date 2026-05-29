import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
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
              '2. If the summary shows errors or a failure, escalate: use search_events_semantic ' +
              '(TIER 1) for the failing area, then get_session_events (TIER 2) for the full ' +
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
                '(fields="sessionId,status,agentVersion,startedAt"). Sweep each version line with agentVersionPrefix ' +
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
