# Rule presentation — shared cases

An analyze rule's optional `presentation` block (role, summary, subject, key facts) is stored as
raw `{{token}}` templates, like the explanation. `cases.json` is the one definition of how a
renderer resolves it against a finding's `matchedConditions`: which facts are left out, when the
summary falls back to the explanation, and when a subject counts. Token resolution itself is the
one in `../rule-template-interpolation`.

Implementations that run every case:

- Web — `resolvePresentation` in `src/Web/autopilot-monitor-web/lib/rulePresentation.ts`
  (test: `lib/__tests__/rulePresentation.test.ts`)
- MCP server — the generated copy `src/McpServer/autopilot-monitor-mcp/src/generated/rule-presentation.generated.ts`
  (test: `src/__tests__/rule-presentation.test.ts`)

A change to the resolution goes into `cases.json` first.
