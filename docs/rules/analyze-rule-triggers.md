---
type: Concept
title: Analyze Rule Evaluation Triggers (evaluateOn)
description: "Opt-in interim evaluation of analyze rules before a session is terminal — enrollment_end stays the default, whiteglove_sealed and on_event:<type> triggers close the WhiteGlove notification gap and the never-terminal stuck-session gap with one mechanism, backed by update-semantics on RuleResults instead of the permanent dedupe freeze."
resource: backend/rule-engine
tags: [rules, analyze, triggers, notifications, whiteglove]
timestamp: 2026-08-14
---

# Status

**IMPLEMENTED 2026-08-14** (same day the spec was approved). The open product decisions below were confirmed as "no" for v1. Key implementation anchors: `AnalyzeRuleTriggers` (Shared — grammar + matching helpers, shared by engine, ingest registry and CRUD validation), `AnalyzeRunContext` / `AnalysisOutcome.ResolvedResults` (RuleEngine), `InterimTriggerRegistry` (5-min-TTL per-tenant cache for the ingest hot path, fail-soft), `AnalyzeOnEnrollmentEndHandler` interim branch, `EvaluateOnJson` + five RuleResult lifecycle columns in `TableStorageService.Rules`. ANALYZE-ID-004 ships as the first interim-enabled rule (`on_event:hybrid_login_pending`, repetition-gated at threshold 75).

# Problem

Analyze rules run at exactly three points today, all of them terminal or near-terminal:

1. `enrollment_complete` / `enrollment_failed` observed at ingest (`EventIngestProcessor.cs`, classification → analyze envelope).
2. The maintenance sweep terminalizing a silent session (Failed / Incomplete + synthetic `session_timeout`, `MaintenanceService.cs`).
3. The vulnerability-correlation rerun (`vulnerability_correlated` reason — incremental, no stats).

Two incident classes fall through this model:

- **WhiteGlove gap (sits-d, 2026-08-11):** `whiteglove_complete` seals a session to Pending but enqueues no analyze run. A rule that should alert at the end of pre-provisioning (technician still at the bench) never fires, and when Part 2 eventually terminalizes days later, the permanent per-(session, rule) dedupe means a finding that sneaked in earlier (e.g. via the vuln-rerun backdoor) is frozen and never notifies.
- **Never-terminal stuck sessions (esa-logistics, 2026-08-13/14):** a session looping in Account Setup (user signs in daily, hybrid affinity never establishes) resets the 5h inactivity clock on every logon. It is never terminalized, therefore never analyzed — the exact sessions where a diagnosis would help most (ANALYZE-ID-004, ANALYZE-APP-015) produce no finding while the customer watches the device loop.

Both gaps share one root cause: *evaluation time is hard-wired to session end*. Gather rules already solved the analogous problem on-device with declarative triggers (`startup` / `phase_change` / `phase_exit` / `interval` / `on_event`); analyze rules need the server-side equivalent.

# Design Overview

Add an optional `evaluateOn` array to the analyze-rule schema. Absent field ⇒ `["enrollment_end"]` ⇒ **today's behavior, bit-for-bit, for every existing rule**.

```
"evaluateOn": ["enrollment_end", "on_event:hybrid_login_pending"]
```

Trigger grammar (v1):

| Trigger | Fires when | Notes |
|---|---|---|
| `enrollment_end` | terminal analyze run (complete / failed / sweep-terminalized) | default; also the **finalization pass** for interim results (below) |
| `whiteglove_sealed` | ingest classifies the first genuine `whiteglove_complete` (session → Pending, `isPreProvisioned`) | duplicate seals do not re-trigger |
| `on_event:<eventType>` | an ingest batch contains ≥1 event of `<eventType>` | `<eventType>` validated against the event-types catalog; phase boundaries are covered by `on_event:phase_transition` / `on_event:esp_phase_changed` — no separate phase grammar needed, since every run evaluates the full stream anyway |

Explicitly **not** in v1: interval/timer triggers (no cheap host for them; the sweep remains the only clock), and `on_event` filters on data fields (the rule's own conditions do that — the trigger only decides *when* to look).

# Pipeline Changes

## Ingest (producer side)

- `EventIngestProcessor` already classifies each stored batch. After classification, intersect the batch's event types with the tenant's **trigger registry** (the set of `on_event` types across the tenant's active rules — derivable from `AnalyzeRuleService`'s merged rule load, cached alongside it). Non-empty intersection ⇒ enqueue one envelope `Reason=interim_trigger`, carrying the matched trigger event types. Reuse the existing 30s visibility delay (terminal-flush ordering argument in `AzureQueueAnalyzeOnEnrollmentEndProducer` applies to interim batches too).
- The `whiteglove_complete` seal path (`EventIngestProcessor.Classification.cs`) enqueues `Reason=whiteglove_sealed` — only on the first seal, only for `isPreProvisioned`.
- Terminal enqueue (`CompletionEvent` / `FailureEvent`) is unchanged.

## Handler / engine (consumer side)

`AnalyzeOnEnrollmentEndHandler` branches by reason today (primary vs. `vulnerability_correlated`); interim reasons become a third branch. The engine call gets a run context instead of the bare `reanalyze` flag:

| Aspect | Terminal run (`enrollment_end`) | Interim run (`whiteglove_sealed`, `interim_trigger`) |
|---|---|---|
| Rule set | ALL active rules (interim-triggered ones get their finalization pass here) | only rules whose `evaluateOn` matches the reason / one of the batch's trigger types |
| KO (`MarkSessionAsFailed`) | applies (unchanged) | **suppressed** — a Pending/InProgress session must never be flipped Failed by an interim pass (`TryMarkSessionFailedFromRuleAsync` is skipped; the terminal run re-fires the rule and applies KO then) |
| Platform stat `IssuesDetected` | counted | skipped (like vuln rerun) |
| Per-rule fire stats | counted | skipped (like vuln rerun) — keeps the terminal-only stats convention and the Rule Regression Radar baselines clean |
| Channel notifications | new findings notify (unchanged) | new findings notify — **this is the point** of both gaps |
| SignalR results-available | yes | yes (live update of an open session page) |

## Result lifecycle: update semantics replace the dedupe freeze

Today the engine skips any rule that already has a stored `RuleResult` row for the session (`RuleEngine.AnalyzeSessionAsync`, existing-results lookup) — findings are frozen at first write, which is precisely the WG-gap failure mode ("dedupe permanent").

New model — `RuleResult` gains four fields (`FirstDetectedAt`, `LastEvaluatedAt`, `IsInterim`, `ResolvedAt`, plus `NotifiedAt` for the notification marker):

- **Interim run:** rules in scope are (re-)evaluated even when a row exists. Fired ⇒ upsert (evidence/confidence refreshed, `FirstDetectedAt` preserved, `IsInterim=true`). Not fired + existing interim row ⇒ `ResolvedAt` set (kept for audit; excluded from issue counts and default UI).
- **Terminal run:** every rule that has an interim row is re-evaluated regardless of dedupe (finalization). Fired ⇒ row finalized (`IsInterim=false`, evidence refreshed — fixes the stale-template-field freeze, e.g. the ESP-004 `{{appName}}` case). Not fired (typically a `not_exists` precondition on `enrollment_complete` now failing because the session healed) ⇒ `ResolvedAt` set. Rules without interim rows keep today's skip-if-exists dedupe.
- **Notification dedupe** moves off "row exists" onto `NotifiedAt` on the row: notify once per (session, rule) at first fire — interim or terminal, whichever comes first; refreshes and finalization do not re-notify. The manual `reanalyze=true` path must **preserve** `NotifiedAt` across its rebuild (today it deletes rows; delete-and-recreate would silently re-arm notifications) and keeps its designed never-notify behavior.
- Legacy rows (null new fields) read as final/terminal — no migration.

# Authoring Guardrails (validate_rule lints)

Interim evaluation changes what a rule's conditions mean mid-flight. Three new lints in the MCP `validate_rule` pre-flight (and the baked authoring guide):

1. **Terminal-precondition trap (warning):** `evaluateOn` contains an interim trigger AND a precondition is `not_exists` on `enrollment_complete` / `enrollment_failed` / `session_timeout`. Mid-run these pass trivially — the rule needs conditions that are *monotonic* (once true on the growing event stream, stays true) or repetition-gated. Worked example: ANALYZE-APP-015 must NOT get an interim trigger as-is — even successful sessions transiently show its "completion signal deferred" + `pending>=1` pattern (the terminal precondition is its only gate). ANALYZE-ID-004 is interim-safe by construction (threshold 65 requires repeated `hybrid_login_pending`).
2. **Unknown trigger event type (error):** `on_event:<type>` not in the event-types catalog.
3. **KO-on-interim (warning):** `markSessionAsFailedDefault: true` on a rule whose only triggers are interim — the KO would never apply until terminal; say so.

# Cost & Safety

- Interim runs are bounded by trigger frequency, and the chosen v1 triggers are rare by construction (`hybrid_login_pending` ≤1 per boot, `whiteglove_sealed` once per session). High-frequency telemetry event types (performance_snapshot, agent_metrics_snapshot, download_progress, network churn, log_entry, agent_trace, stall_probe_check) are **hard-blocked** as `on_event` triggers, not merely advised against: the CRUD write is rejected (`AnalyzeRuleTriggers.BlockedOnEventTypes`, code-only enforcement), the runtime matcher drops them from any row that slipped in (defense in depth — the trigger registry and the engine both go through `OnEventTypes`), and the MCP lint reports them as an error. The JSON mirror lives in `rules/guardrails.json` (`blockedInterimTriggerEventTypes`) with a parity test pinning it to the C# set — the same pattern the gather hard blocks use. If a legitimate rare-but-bursty type ever hurts, a per-(session, reason) throttle marker is the v2 lever.
- Each run loads the full event stream once (existing engine behavior, no change); rule filtering makes interim passes strictly cheaper than terminal ones.
- Queue semantics (retry via visibility timeout, poison after MaxDequeueCount, idempotent partial-success re-runs) are inherited unchanged; upserts keep idempotency.

# Surfaces

- **Web session page:** interim findings render live (existing SignalR results-available notification) with a "preliminary" badge on `IsInterim`; `ResolvedAt` rows hidden by default behind a toggle.
- **MCP:** `get_session(includeAnalysis)` / `get_session_summary` expose `isInterim` / `resolvedAt`; `list_session_annotations`' fired-rule snapshot semantics unchanged (labels bind to whatever was fired at annotation time).
- **Success-rate / platform stats:** terminal-only convention untouched (interim runs record no stats).

# Application to the Motivating Cases

- WG battery rule (sits-d): `evaluateOn: ["enrollment_end", "whiteglove_sealed"]` — technician gets the channel alert at the bench; Part-2 terminal pass finalizes or resolves the finding.
- Hybrid-affinity stuck (esa): ANALYZE-ID-004 gets `evaluateOn: ["enrollment_end", "on_event:hybrid_login_pending"]` — the second overdue-login event (threshold 65) produces a live finding on the still-InProgress session. ANALYZE-APP-015 stays `enrollment_end`-only until its conditions are hardened (lint 1).

- Stuck after a forced mid-ESP reboot (sits-d Cloud PCs, 2026-08-19): `on_event:session_stalled` is the established trigger for the never-terminal stuck session. The agent emits `session_stalled` exactly once per session (60-minute stall probe), so the trigger is rare by construction and needs no throttle. Five Cloud PCs sat at `InProgress` with **zero findings** for hours — a mid-ESP reboot had killed the agent in `Stage=EspAccountSetup` — before the max-lifetime watchdog eventually terminalised them as `Incomplete`. ANALYZE-ESP-005 carries the trigger and fires on that stream at confidence 95 (verified against session `8110e262`): both its conditions (`mdm_policy_reboot_required.firstRebootUri` exists, `system_reboot_detected` count ≥ 1) are **monotonic** and it has no terminal preconditions, so lint 1 does not apply. ANALYZE-APP-016 is interim-safe for the same reason (`app_install_starved` count ≥ 1 only ever grows).

  Two rules were considered and deliberately **not** given the trigger: ANALYZE-APP-015 is the worked example of lint 1 above and stays `enrollment_end`-only; ANALYZE-ESP-001 gates on `phase_duration` of a phase that is still open mid-run, and that evaluation is not pinned by a test — an unverified trigger is not worth a possible false positive on every slow DeviceSetup.

# Product Decisions (settled 2026-08-14)

1. Notify-on-resolve (a "finding healed" message)? **No** (v1) — resolution is visible in UI/MCP.
2. Re-notify on severity/confidence escalation of an existing finding? **No** (v1) — one notification per finding.
3. `whiteglove_sealed` runs allowing KO rules to flip Pending → Failed? **No** — the Pending state is the WhiteGlove contract; suppression is unconditional for interim.

# Citations

- `src/Backend/AutopilotMonitor.Functions/Services/EventIngestProcessor.cs` — terminal-only enqueue (classification → analyze envelope).
- `src/Backend/AutopilotMonitor.Functions/Services/Analyze/AnalyzeOnEnrollmentEndHandler.cs` — reason branching, notify path, stats side-effects.
- `src/Backend/AutopilotMonitor.Functions/Services/Analyze/AzureQueueAnalyzeOnEnrollmentEndProducer.cs` — 30s visibility delay rationale.
- `src/Backend/AutopilotMonitor.Functions/Services/RuleEngine.cs` — existing-results dedupe, `TryMarkSessionFailedFromRuleAsync` KO path.
- `src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.cs` — sweep terminalization + analyze enqueue; inactivity-clock reset explains the never-terminal gap.
- `rules/gather-rule-phase-scoping.md` (this bundle) — the on-device trigger precedent this design mirrors.
- `rules/rule-authoring-surface.md` (this bundle) — where the new lints land (validate_rule + baked guide).
