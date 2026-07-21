---
type: Concept
title: Gather Rule Phase Scoping, One-Shot Triggers & Emit-on-Change
description: How gather rules are restricted to enrollment phases (activePhases / activeFromPhase), collect once at a phase boundary (phase_change / phase_exit), and how emitMode "on_change" suppresses repeated identical results.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleExecutor.cs
tags:
  - agent
  - backend
  - gather-rules
  - config
timestamp: 2026-07-20T00:00:00+02:00
---

# Problem

Interval gather rules (e.g. a registry poll every 60 s) ran during **all** enrollment phases
and emitted an event on **every** tick — including `exists=false` events while the target key
did not exist yet. A rule needed only from Account Setup onwards produced hundreds of
identical noise events per session and wasted collector runs.

# Schema

Three opt-in fields on `GatherRule` (Shared model, `rules/schema/gather-rule.schema.json`,
delivered to the agent via `GET agent/config`; ConfigVersion 34). Absent fields = legacy
behavior — full back-compat.

| Field | Type | Semantics |
|---|---|---|
| `activePhases` | `string[]` \| null | Rule runs only while the current phase is in the list. Null/empty = unrestricted. |
| `activeFromPhase` | `string` \| null | Sticky latch: activates when the phase first reaches the threshold, then stays active for the rest of the session — including through `Failed`. Null = unrestricted. |
| `emitMode` | `"always"` \| `"on_change"` \| null | `on_change`: poll on the trigger cadence, emit only when the collected result changes. Null/`always` = emit every collection (legacy). |

Canonical phase tokens are the `EnrollmentPhase` enum **names** `Start`(0) …`Complete`(7).
Backend validation (`GatherRulesFunction.ValidateScopeAndEmitMode`) rejects `Unknown`/`Failed`,
numeric tokens, unknown emit modes, and both scope fields set at once (400). The agent
defensively prefers `activePhases` if both arrive anyway.

# One-shot collection at phase boundaries

Scoping answers "while *may* this rule run". The trigger answers "when does it fire". For
one-shot collection the two phase triggers are the bookends of a phase:

| Trigger | Fires |
|---|---|
| `phase_change` | Once when `triggerPhase` is **entered** (dedup key `{ruleId}\|{phase}`). |
| `phase_exit` | Once when `triggerPhase` is **left** (dedup key `exit:{ruleId}\|{phase}`). |

Empty `triggerPhase` means every transition. Both keys live in the same
`_phaseRulesExecuted` set; the `exit:` prefix keeps the two key spaces disjoint, so an
enter-rule and an exit-rule on the same phase never cannibalise each other's slot.

`phase_exit` semantics that are easy to get wrong:

* It is evaluated **before** `_currentPhase` advances, so both the `triggerPhase` match and
  the phase-scope gate see the phase being **left**. A rule scoped to `DeviceSetup` therefore
  still fires on its own exit, even though the phase being entered is outside that scope.
* A transition into `Failed` **does** fire the exit rules of the failing phase — capturing
  state at the failure boundary is the point.
* Exit of `Unknown` never fires (nothing was entered yet), and a repeated identical phase
  signal is not a transition.
* An exit rule on `Complete` only fires if something transitions *away* from Complete. For
  "at the end of enrollment", use `phase_change` on `Complete` or `on_event` with
  `enrollment_complete`.
* Old agents do not know the trigger value and simply never fire the rule (silent no-op) —
  which is why `phase_exit` needs no ConfigVersion bump: no new field is transported.

# Scope semantics (agent, `GatherRuleExecutor`)

* Scope applies to **all trigger types**:
  * `interval` — timers keep running; an out-of-scope tick is a free early-return (no
    start/stop lifecycle races on phase transitions).
  * `startup` — a scoped startup rule not yet in scope at `UpdateRules` is **deferred**: it
    runs exactly once from `OnPhaseChanged` when its scope activates (same
    `_startupRulesExecuted` dedup; it is not part of the `WaitForStartupRules` latch).
  * `phase_change` — evaluated against the **new** phase; the scope gate runs before the
    per-(rule, phase) dedup so an out-of-scope pass does not consume the execution slot.
  * `on_event` — gated by the current phase.
* **Before the first phase signal** of a session (`_currentPhase == Unknown`) scoped rules are
  inactive. Sessions that never produce phase signals never run them (documented behavior).
* **From-phase latch**: activates when `phase != Unknown && phase != Failed && (int)phase >=
  (int)fromPhase`. `Failed=99` would ordinal-satisfy every threshold, hence the explicit
  exclusion — but once latched, a rule stays active through `Failed`. A config refresh that
  delivers a from-phase rule after its threshold passed latches it immediately.
* `--run-gather-rules` (diagnostic CLI) sets `IgnorePhaseScope = true` — no phase context
  exists there, scoped rules execute unconditionally (log line marks the bypass).

# Emit-on-change semantics

* The collector result dict is canonically hashed (keys ordinal-sorted at every nesting
  level, invariant value formatting, SHA-256) **before** `ruleId`/`ruleTitle` injection.
* First in-scope non-empty result always emits — on an absent registry key that first
  `exists=false` event *is* the visible "we're polling now" indicator.
* Unchanged results increment a per-rule suppression counter; the next emitted event carries
  `suppressedPolls` + `suppressedSinceUtc` in its `Data` so the gap is observable.
* Composition with `emitOnlyIfExists=true` (collector-level): a miss returns an empty dict —
  no emit AND no hash update. Result: zero noise while absent, one event on appearance, then
  only on change.
* State is in-memory and survives `UpdateRules` config refreshes; an agent restart causes at
  most one re-emit per rule (acceptable under the per-enrollment lifecycle).
* Volatile collectors (eventlog timestamps, logparser positions) degrade `on_change` to
  `always` — every poll hashes differently. The option stays visible for them since some
  outputs are stable; this is documented, not hidden.

# Portal & delivery

* Create form defaults new rules to `emitMode: "on_change"`; existing rules load as
  `always`. The scope UI is a three-way mode (always / during phases / from phase) backed by
  a canonical `GATHER_PHASES` list — the same list replaced the free-text `triggerPhase`
  input, whose placeholder used to suggest invalid tokens.
* The custom-rule toggle PUT (`{enabled, isBuiltIn, isCommunity}`) previously full-replaced
  the stored rule and wiped Title/Target/Trigger — `GatherRuleService.UpdateRuleAsync` now
  merges toggle-style partials (empty Title/CollectorType/Target) into the existing row and
  preserves `CreatedAt` on full edits. This fix is a prerequisite: without it every toggle
  would silently clear the new fields.
* Old agents (Newtonsoft) ignore unknown config properties: scoped rules run everywhere,
  `emitMode` is ignored — graceful degradation until the fleet rolls forward.
* `ContentEquivalent` treats null and empty `activePhases` as equal, so pre-existing DB rows
  (absent columns → null) never diff against seeds — no reseed churn.

# Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleExecutor.cs` — scope gates, latch, canonical hash
* `src/Backend/AutopilotMonitor.Functions/Functions/Rules/GatherRulesFunction.cs` — `ValidateScopeAndEmitMode`
* `src/Backend/AutopilotMonitor.Functions/Services/GatherRuleService.cs` — partial-PUT merge, `ContentEquivalent`
* `src/Shared/AutopilotMonitor.Shared/Models/Rules/GatherRule.cs` — field contracts
* Tests: `GatherRuleExecutorPhaseScopeTests`, `GatherRuleExecutorEmitModeTests` (agent); `GatherRuleUpdatePartialMergeTests`, `GatherRuleScopeFieldsTests` (backend)
