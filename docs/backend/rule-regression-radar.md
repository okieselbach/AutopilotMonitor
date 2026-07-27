---
type: Concept
title: Rule Regression Radar (F3)
description: How the radar detects analyze rules whose hit rate suddenly rose (7-day window vs 28-day baseline, Wilson-separated), which suppressions keep it quiet, how alert episodes live in the notification tracker, and which surfaces show them.
resource: src/Backend/AutopilotMonitor.Functions/Helpers/RuleRegressionRadar.cs
tags:
  - backend
  - rules
  - regression-radar
  - insights
timestamp: 2026-07-27T16:00:00+02:00
---

# Rule Regression Radar (F3)

Detects "this analyze rule suddenly fires much more often" before helpdesk volume does, and
says what the spike correlates with — with honest statistics and honest wording. Analyze
rules only in v1: their daily stats are audit-verified session-deduplicated
(`FireCount` ≈ distinct sessions with a hit, `SessionsEvaluated` the same-source
denominator). Gather rules are excluded until their per-batch stats dedup is fixed —
never silently included with bad math.

# Schema

## Signal & detection

Per (tenant, ruleId), daily hit rate = `FireCount ÷ SessionsEvaluated` from the
`RuleStats` rows — RATES, not counts (absolute counts spike with every rollout wave).
A regression fires only when ALL hold for the trailing 7-day window vs. the prior
28-day baseline:

1. window has ≥ 5 hit sessions AND ≥ 20 evaluated sessions;
2. window rate ≥ 2× baseline rate;
3. the Wilson 95 % intervals are disjoint in the increase direction
   (`MetricsMath.WilsonInterval` / `RateIncreaseSeparated` — deterministic, unit-pinned),
   so small-n noise never alerts.

## Suppression (false positives are trust damage)

* Rule entity missing (deleted) — never alerts.
* Rule younger than baseline + window (`CreatedAt` within 35 days — grace period for new
  rules; the entity timestamp is the honest gate, a stats-row count would fail on
  low-volume tenants whose quiet days simply have no rows).
* Rule edited inside the window (`UpdatedAt` ≥ window start) — edits legitimately change
  hit rates.
* Empty baseline denominator (rule disabled throughout the baseline).
* Platform kill switch: app setting `RuleRegressionRadarDisabled=true` skips the radar
  (fail-open — it only notifies, never mutates data).

## Alert episodes (tracker keyspace)

An episode lives as `ruleregression|{ruleId}` in the notification-tracker table
(the `tpmpss|` pattern): the row IS the dedup (bell + ops event fire exactly once per
episode), the badge state and the `regressions[]` payload — window/baseline counts,
rates, lift (null for a zero-baseline "new signal" — no finite lift is invented), window
dates, and the dimension concentration captured at first fire. Numbers refresh on every
radar pass while the episode stays active; `FirstNotifiedAt` never moves. The episode
closes (badge clears, dedup re-arms) when the window rate falls back under 1.5× baseline
or fires stop entirely; the tracker's 30-day retention sweep also re-arms, so a
month-old still-burning regression rings once more by design.

## Dimension correlation (on fire only)

Hit sessions (RuleResults scan: tenant partition range + RowKey = ruleId + DetectedAt in
window, intersected with the window's sessions) are compared against all window sessions
across `osBuild`, `model` (manufacturer + model), `agentVersion` and `imeVersion`. The
top value by lift is reported with BOTH shares — wording contract: "X % of affected
sessions are on … vs Y % of all sessions — correlated, not necessarily causal". No value
with ≥ 5 hit sessions at ≥ 2× lift → "no clear dimension concentration"; a correlation
failure also yields no claim. App-version correlation for `ANALYZE-APP-*` rules is a
follow-up (needs rule→app matching semantics), tracked with the gather-rule fix.

## Compute & alert path

Runs in the daily maintenance pass right after the rule-stats aggregation
(`RunRuleRegressionRadarAsync`), anchored on YESTERDAY — whole days only, a partial
today would understate the window rate. One 35-day stats read covers all tenants;
evaluation is the pure `RuleRegressionRadar` core. On a NEW episode:

1. `RuleFrequencyRegression` ops event (category Tenant, Warning) with the full numbers
   — dual-registered in the web `OPS_EVENT_TYPES` catalog for alert-rule routing.
2. Tenant bell notification (type `rule_frequency_regression`, Admin audience) with the
   same numbers and a deep link to the rule's card (`/analyze-rules#rule-card-{ruleId}`).
   A sessions-list pre-filtered to rule hits needs a rule filter the session search does
   not have yet — follow-up alongside the gather-rule work.
3. v1 channel: bell + ops event only. Provider fan-out (webhook/Telegram) follows once
   alert quality is proven — a noisy first version burns trust permanently.

# Surfaces

* `GET api/metrics/rule-stats` (and the global variant with `tenantId`) carries
  `regressions[]` — the active episodes. MCP `get_rule_stats` inherits the block.
* Analyze-rules page: per-rule 30-day fire-count sparkline (densified from the `trend`
  rows the endpoint already served) and a red "↑ Regression" badge while an episode is
  active, with the full numbers in the tooltip.

# Examples

A rule firing on 2 % of sessions for a month jumps to 15 % over a week (15/100 vs
10/500): lift 7.5×, Wilson-separated → one bell, one ops event, badge until the rate
falls back under 3 %. The same jump on 5/20 sessions with a 12 % baseline stays silent —
the intervals overlap, so the spike is not statistically real yet. A rule that never
fired in the baseline and now hits 15 % alerts as a "new signal" with no lift figure.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Helpers/RuleRegressionRadar.cs` — gates, suppression, re-arm, dimension concentration.
* `src/Backend/AutopilotMonitor.Functions/Helpers/MetricsMath.cs` — Wilson interval + separation primitive.
* `src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.RuleRegression.cs` — sweep, episode reconcile, bell/ops wiring.
* `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableHardwareRejectionNotificationTracker.cs` — episode keyspace.
* `src/Backend/AutopilotMonitor.Functions.Tests/RuleRegressionRadarTests.cs` — hand-computed Wilson vectors + every suppression branch.
* `tasks/insights-expansion-spec.md` — F3 specification and source-data audit (§0.5).
