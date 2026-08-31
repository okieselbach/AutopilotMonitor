import { McpServer } from '@modelcontextprotocol/server';
import { z } from 'zod';
import { apiFetch } from '../client.js';
import { withToolTelemetry } from '../telemetry.js';
import { READ_ONLY, MAX_RESULT_SIZE_CHARS, toolResultText, SessionIdSchema } from './shared.js';
import { toolError } from './error-handler.js';
import { validateRuleDraft } from '../rule-validation.js';
import { interpolateRuleTemplate } from '../interpolate-rule-template.js';
import type { DryRunAnalyzeRuleResponse } from '../generated/wire-types.generated.js';

/**
 * Rule-authoring tools: validate a draft gather/analyze rule locally, and dry-run a
 * draft analyze rule against a real session via the backend's side-effect-free
 * dryrun endpoint. Registered for EVERY role — tenant admins are the primary
 * audience (the backend still enforces its own authorization: the dryrun route is
 * TenantAdmin / Global Admin / read-only Global Reader).
 */
export function registerRuleTools(server: McpServer, ga: boolean): void {
  // Tool: validate_rule (MCP-local, no backend call)
  server.registerTool(
    'validate_rule',
    {
      title: 'Validate Rule Draft',
      description:
        'Validates a DRAFT gather or analyze rule without deploying anything: JSON-Schema check ' +
        '(the exact contract from get_resource(name="rule_schemas")), guardrail pre-flight for ' +
        'gather targets (registry/file/WMI/command/event-log allowlists with the agent\'s ' +
        'matching semantics), and semantic lint (unreachable confidence threshold, non-evaluable ' +
        'confidenceFactor conditions, unknown event types, unanchored allow-list regex, ' +
        'unresolvable {{token}} placeholders). Returns findings as error/warning/info. ' +
        'Fix all errors, then dry-run analyze rules with test_analyze_rule against a real session. ' +
        'Read get_resource(name="rule_authoring_guide") first when authoring from scratch.',
      inputSchema: {
        rule: z.record(z.string(), z.unknown()).describe('The draft rule JSON object (gather or analyze — detected automatically)'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('validate_rule', args, async () => {
      try {
        const result = validateRuleDraft(args.rule as Record<string, unknown>);
        return toolResultText(
          {
            ruleType: result.ruleType,
            valid: result.valid,
            errorCount: result.findings.filter((f) => f.level === 'error').length,
            findings: result.findings,
            nextStep: result.valid
              ? (result.ruleType === 'analyze'
                ? 'No errors. Dry-run it now: test_analyze_rule(sessionId=<recent session>, rule=<this draft>) — once against a session where it SHOULD fire, once where it should not.'
                : (args.rule as Record<string, unknown>).collectorType === 'logparser'
                  ? 'No errors. Now test the regex with the agent\'s exact .NET engine: test_log_pattern(pattern=<parameters.pattern>, sampleLines=<raw lines from the log file>, format=<parameters.format>) — then deploy to a test device.'
                  : 'No errors. Gather rules run on-device and cannot be dry-run server-side — deploy to a test device and check the emitted events (the gather debug log setting shows per-rule detail).')
              : 'Fix the error-level findings and validate again.',
          },
          MAX_RESULT_SIZE_CHARS.small,
        );
      } catch (error: unknown) {
        return toolError('validate_rule', args, error);
      }
    })
  );

  // Tool: test_log_pattern (backend .NET-regex test for logparser gather rules)
  server.registerTool(
    'test_log_pattern',
    {
      title: 'Test Logparser Pattern Against Sample Lines',
      description:
        'Tests a logparser gather-rule regex against pasted sample log lines using the AGENT\'s ' +
        'exact .NET matching semantics — the dry-run for logparser rules (which run on devices ' +
        'and cannot be tested against a session). Use this INSTEAD of testing the regex in ' +
        'JS/PHP/Python: .NET behaves subtly differently, and logparser matching is ' +
        'case-SENSITIVE (unlike analyze-rule regex conditions). Paste 10-50 representative raw ' +
        'lines from the customer\'s log file (include lines that must match AND lines that must ' +
        'not). format="cmtrace" (default) parses each line as CMTrace/IME format first and runs ' +
        'the regex against the parsed message; format="text" matches the raw line. Returns ' +
        'per-line outcomes with the exact capture groups that would land in the emitted ' +
        'timeline event\'s data. Nothing is stored.',
      inputSchema: {
        pattern: z.string().min(1).max(2000).describe('The regex (named groups become event data fields), .NET syntax'),
        sampleLines: z.array(z.string().max(8192)).min(1).max(200)
          .describe('Raw lines pasted from the log file (max 200)'),
        format: z.enum(['cmtrace', 'text']).optional()
          .describe('Log format, exactly like the rule\'s parameters.format: "cmtrace" (default, IME/SCCM style) or "text" (plain lines)'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('test_log_pattern', args, async () => {
      try {
        const data = await apiFetch('/api/rules/gather/test-pattern', {
          method: 'POST',
          body: JSON.stringify({ pattern: args.pattern, format: args.format, sampleLines: args.sampleLines }),
        });
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('test_log_pattern', args, error);
      }
    })
  );

  // Tool: test_analyze_rule (backend dry-run, side-effect free)
  server.registerTool(
    'test_analyze_rule',
    {
      title: 'Test Analyze Rule Against Session (Dry-Run)',
      description:
        'Dry-runs a DRAFT analyze rule against one real session\'s events and returns the full ' +
        'diagnostic trace: verdict (fired / required_condition_not_met / below_confidence_threshold ' +
        '/ skipped_by_precondition / no_conditions_matched / no_events), per-condition ' +
        'matched/evidence with matchingEventCount (how many session events even have that ' +
        'eventType — the first thing to check when a condition unexpectedly misses), the ' +
        'confidence breakdown per factor, and the interpolated explanation preview. ' +
        'NOTHING is persisted — safe to iterate. Use a recent session (older sessions predate ' +
        'newly deployed gather rules). Test both directions: a session where the rule should ' +
        'fire AND one where it should stay silent.' +
        (ga ? ' Platform-scope callers can target any tenant\'s session — the tenant is resolved from the session automatically.' : ''),
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID to evaluate the draft against'),
        rule: z.record(z.string(), z.unknown()).describe('The draft analyze rule JSON object'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('test_analyze_rule', args, async () => {
      try {
        const data = await apiFetch('/api/rules/analyze/dryrun', {
          method: 'POST',
          body: JSON.stringify({ sessionId: args.sessionId, rule: args.rule }),
        }) as DryRunAnalyzeRuleResponse;

        // `result` is `unknown` on the wire by design (the RuleDryRun trace lives in the
        // Functions assembly) — read the two fields this tool needs via a local view.
        const result = data.result as { verdict?: string; matchedConditions?: Record<string, unknown> } | undefined;
        const rule = args.rule as Record<string, unknown>;
        const mc = result?.matchedConditions;

        // Preview the explanation/remediation exactly as the portal would render them
        // for this session's evidence ({{token}} interpolation).
        const interpolatedExplanation = typeof rule.explanation === 'string'
          ? interpolateRuleTemplate(rule.explanation, mc)
          : undefined;
        const interpolatedRemediation = Array.isArray(rule.remediation)
          ? (rule.remediation as Array<{ title?: string; steps?: string[] }>).map((step) => ({
              title: interpolateRuleTemplate(step?.title, mc),
              steps: (step?.steps ?? []).map((s) => interpolateRuleTemplate(s, mc)),
            }))
          : undefined;

        return toolResultText(
          { ...data, interpolatedExplanation, interpolatedRemediation },
          MAX_RESULT_SIZE_CHARS.small,
        );
      } catch (error: unknown) {
        return toolError('test_analyze_rule', args, error);
      }
    })
  );
}
