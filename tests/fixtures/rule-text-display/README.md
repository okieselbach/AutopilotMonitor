# Rule text display — shared cases

A finding's explanation is the rule's raw template; the reader sees it either as a notification
(no renderer) or in the portal (markdown renderer). Both prune it the same way before showing it:
a list item whose `{{token}}` was not recorded is dropped, a token in running text that was not
recorded shows as `–`, and a sentence that still talks about literal placeholders is removed.
`cases.json` holds the expected text with the recorded values filled in.

Implementations that run every case:

- Backend — `RuleTemplateInterpolator.InterpolateForNotification`
  (test: `RuleTemplateInterpolatorTests.InterpolateForNotification_prunes_like_the_shared_display_case`)
- Web — `pruneRuleText` in `src/Web/autopilot-monitor-web/lib/ruleText.ts`, which keeps the
  template for the markdown parser and fills values into text nodes only afterwards
  (test: `lib/__tests__/ruleText.test.ts`)

Token resolution itself is the one in `../rule-template-interpolation`. A change to the pruning
goes into `cases.json` first.
