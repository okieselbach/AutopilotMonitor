# Rule template interpolation — shared cases

A rule result stores the rule's text with its `{{token}}` placeholders; each renderer resolves
them from the result's `matchedConditions`. `cases.json` is the one definition of that
resolution, and every implementation runs all of it:

- Web — `interpolateRuleTemplate` in `src/Web/autopilot-monitor-web/lib/interpolateRuleTemplate.ts`
  (test: `lib/__tests__/interpolateRuleTemplate.test.ts`)
- MCP server — `interpolateRuleTemplate` in `src/McpServer/autopilot-monitor-mcp/src/interpolate-rule-template.ts`
  (test: `src/__tests__/interpolate-rule-template.test.ts`)
- Backend — `RuleTemplateInterpolator.Interpolate` in
  `src/Backend/AutopilotMonitor.Functions/Services/RuleTemplateInterpolator.cs`
  (test: `RuleTemplateInterpolatorTests.cs`)

`autoFields` is the list of same-event fields the rule engine copies into every evidence
entry (`RuleEngine.EvidenceAutoFields`). Each implementation's list is compared against it.

A change to the resolution goes into `cases.json` first; the three tests then say which
implementation is behind.
