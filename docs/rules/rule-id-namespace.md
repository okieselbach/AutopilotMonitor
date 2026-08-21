---
type: concept
title: Rule ID Namespace & Collision Guards
description: How gather/analyze rule identity is scoped (tenant partition vs global partition), why there are no GUIDs, which collisions are possible, and the guards that keep the reserved built-in namespace and the global RuleStats aggregate clean.
resource: backend, rules
tags: [rules, ruleid, namespace, collision, rulestats, tenant-isolation]
timestamp: 2026-08-21
---

# Schema

Rule identity is the human-readable `ruleId` string — there are no GUIDs, deliberately: the ID appears in logs, mails, notifications, RuleResults, the agent config payload, and docs, and tenant isolation is provided by Table Storage partitioning instead.

| Store | PartitionKey | RowKey | Content |
|---|---|---|---|
| `GatherRules` / `AnalyzeRules` | `"global"` | ruleId | Built-in + community rules (shared, all tenants) |
| `GatherRules` / `AnalyzeRules` | tenantId | ruleId | Custom rules (visible only to that tenant) |
| `RuleStates` | tenantId | ruleId | Per-tenant enable/notify overrides — built-in/community rules ONLY (custom rules fold state into their own row) |
| `RuleStats` | date | `{tenantId}_{ruleId}` and `global_{ruleId}` | Daily fire/eval telemetry; the global row is catalog-only (see guards) |

Namespace split (`RuleIdPolicy.cs`, shared model):

- **Reserved built-in namespace**: `(ANALYZE|GATHER)-<CATEGORY>-<NUMBER>` with `CATEGORY != CUSTOM`, case-insensitive, including currently unused numbers (gaps = retired rules that may return).
- **Tenant namespace**: everything else — `ANALYZE-CUSTOM-NNN` (portal suggestion), `-CUSTOM` suffixes, org prefixes (`CONTOSO-WIFI-001`), freeform.

# Collision model

- **Custom vs built-in**: impossible via API — `CreateRuleAsync`/`UpdateRuleAsync` reject reserved IDs (409) and check existence against `global` ∪ own tenant partition. Legacy debris is neutralized at merge time: global wins, tenant copy dropped with a warning (pinned by `RuleMergeCollisionGuardTests`, motivated by the 5ca2b350 duplicate-key incident).
- **Custom vs custom across tenants**: allowed and harmless — separate partitions, separate evaluation, separate RuleResults/tenant stats. Two tenants both using `ANALYZE-CUSTOM-101` never interact.
- **JSON copy between tenants** (export → paste into create editor): goes through the same create path, so an ID already taken in the receiving tenant surfaces as 409 — the user renames. There is no bulk import endpoint; note the export→paste round-trip is lossy (gather `parameters`, analyze `evaluateOn` are form-derived and silently dropped by the create editor).

# Guards (all enforced, 2026-08-21)

1. **Global RuleStats aggregate is catalog-only.** The `global_{ruleId}` row would otherwise sum same-ID custom rules of unrelated tenants (title/severity last-writer-wins). Analyze gates on `rule.IsBuiltIn || rule.IsCommunity` (`AnalyzeOnEnrollmentEndHandler`), gather gates on `RuleIdPolicy.IsReservedBuiltInId` (`EventIngestProcessor.RuleStats`) because agent events carry no flag — valid because custom rules can never occupy the reserved pattern.
2. **Catalog build fails on namespace violations.** `rules/scripts/combine.js` rejects duplicate IDs (case-insensitive) and any gather/analyze built-in outside the reserved pattern; `BuiltInRuleCatalogPolicyTests` pins the same invariants on the embedded resource actually deployed.
3. **GitHub reseed rejects non-reserved IDs** before writing to the `global` partition (`ReseedFromGitHubFunction`) — a seeded `*-CUSTOM-*` rule would silently shadow every tenant's same-ID custom rule at merge time.
4. **Cross-tenant RuleState GC is namespace-gated.** `DeleteRuleStatesForRuleIdAcrossTenantsAsync` deletes by bare RowKey across all partitions; it now refuses non-reserved IDs with `failed=-1` (callers then skip the global catalog delete, same contract as enumeration failure).

Invariant chain: schema pattern + combine.js + reseed guard ⇒ the `global` partition only ever contains reserved-pattern IDs ⇒ the pattern is a safe built-in/custom discriminator everywhere no `IsBuiltIn` flag is available.

# Citations

- `src/Shared/AutopilotMonitor.Shared/Models/Rules/RuleIdPolicy.cs` — reserved pattern + rationale
- `src/Backend/AutopilotMonitor.Functions/Services/AnalyzeRuleService.cs`, `GatherRuleService.cs` — create/update guards, merge collision guard
- `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Rules.cs` — key schemes, cross-tenant GC guard
- `src/Backend/AutopilotMonitor.Functions.Tests/BuiltInRuleCatalogPolicyTests.cs`, `RuleIdReservationTests.cs`, `RuleMergeCollisionGuardTests.cs`
- `rules/scripts/combine.js` — build-time namespace/duplicate guard
