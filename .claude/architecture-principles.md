# Autopilot-Monitor — Agent Architecture Principles

The principles are **not nice-to-haves**, they are review criteria: any code
change that violates a principle is either (a) a bug, (b) a reason to change the principle
(with user sign-off), or (c) an explicitly documented exception with rationale.

---

## L.1 Truth Hierarchy (immutable)

`SignalLog` (input) → `Journal` (decisions) → `Snapshot` (cache) → `Projections` (derivable).
No projection patches backwards into the journal or the signal log. On inconsistency, the
lower layer always wins (e.g. discard the snapshot, do not "repair" the signal log).

## L.2 Event Sourcing as Foundation

`SignalLog` is the only input truth. The reducer is pure:
`(oldState, signal) → (newState, transition, effects)`. The backend can re-derive at any
time from `SignalLog`. Replay yields the same `Journal` content.

## L.3 Immutable State

All DTOs in the decision core are `sealed` with get-only properties. No in-place mutation.
"Change" = new instance via `With…` method. This is the difference between "purely testable"
and "subtle heisenbugs".

## L.4 Hypothesis Model instead of Flow Plugins

Fuzziness is constitutive (White-Glove, enrollment type, device-only). A single reducer with
hypothesis fields (`Unknown/Weak/Strong/Confirmed/Rejected`), organized by scenario in partial
classes. **No** plugin runtime, because clean flow boundaries do not match reality.

## L.5 Evidence Determinism

Signal-log determinism is guaranteed: same log → same outcome. Every signal carries an
`Evidence` record with `Kind` (Raw / Derived / Synthetic) and mandatory fields. A signal
without evidence = adapter rejection = bug.

## L.6 Signal Schema Versioning

Every `DecisionSignalKind` has a `SchemaVersion`. The reducer dispatches on
`(Kind, SchemaVersion)` — old versions stay replay-capable. A new version or a new kind
without a replay fixture in `tests/fixtures/signal-kinds/{kind}-v{n}.json` = merge block.

## L.7 Timer Monopoly

Only the Deadline Scheduler (the EffectRunner role) holds decision-relevant timers. Collectors,
signal adapters and the orchestrator must not. `Task.Delay` or `new Timer(...)` in those
layers with session-state impact = merge block. `IClock` interface + virtual clock for
testability.

## L.8 Classifier as Post-Hook Kernel Service

`IClassifier` interface, swappable. The `ClassifierVerdict` flows back into the log as a
synthetic `ClassifierVerdictIssued` signal. `InputHash` (SHA256 over classifier-relevant
snapshot fields) is the anti-loop guard. Classifiers are not baked into the state machine.

## L.9 Clean-Agent Principle on Refactors

For structural refactors: build the new project in parallel to the legacy one, do not flag-
route inside the same process. Strict separation rule: no direct reference to legacy
projects. Code is copied, not shared. Legacy runs out, then gets removed.

## L.10 Generic Telemetry Transport

One spool for all telemetry types (events, signals, decision transitions). `TelemetryItemId`
is the type-agnostic upload cursor; the type-specific counters (`Event.Sequence`,
`SignalOrdinal`, `StepIndex`) remain storage / UI / reducer semantics. Drain guarantee:
"coordinated + idempotent + resumable" — **not** cross-table atomic, which is not achievable
with three separate tables anyway.

## L.11 Separation: Agent Lifecycle vs Session State

`DecisionState` is session-pure. Agent-process flags (crash, admin actions, boot time,
heartbeat status) live separately in `agent-lifecycle.json`, managed by the orchestrator,
never by the reducer. Prevents replay dilution by lifecycle events.

## L.12 Flush-on-Append Persistence

No batching of append operations. `FileOptions.WriteThrough` plus an explicit
`FlushAsync(flushToDisk: true)` per append. Every successful `Append` return means: on disk.
Durability > performance in the crash-prone OOBE context.

## L.13 UI Primacy — End-Users vs Dev Tooling

Product views (EventTimeline, PhaseTimeline) stay slim and stable. Debug / analysis tools
(Inspector with DecisionGraph, SignalStream, VerifierReport) live on their own subpage behind
an `adminMode` gate. The session-detail page does not get overloaded with dev components.

## L.14 Forward Compatibility by Default

Enums via `StringEnumConverter` + `UnknownFallbackEnumConverter` (unknown values → default,
never crash). New schema fields are nullable. The `ReducerVersion` stamp per journal row
allows older backends to read newer rows. Deletions only after a major version bump with
user sign-off.

## L.15 Test Discipline

Flow / scenario tests are prioritized over structural tests. Coverage targets per component
(DecisionCore ≥95 %, classifier 100 %, backend functions ≥85 %). A missing fixture for a
scenario = merge block. CI threshold check is a build failure, not a warning.

## L.16 Fail-Safe Reducer Exceptions

Exception in the reducer → `DecisionTransition` with `DeadEndReason=reducer_exception`, state
unchanged (fail-safe, never destroy state). Signal counts as processed (no retry loop). After
five consecutive exceptions → the orchestrator emits
`enrollment_failed(reason=reducer_instability)`.

## L.17 Effect Error Handling by Class

Three effect classes with their own strategy:

- **Transient** (retry with exponential backoff 100 ms / 400 ms / 1600 ms):
  `EmitEventTimelineEntry`, `PersistSnapshot`
- **Critical** (session abort, e.g. on timer-register failure): `ScheduleDeadline`,
  `CancelDeadline`
- **Optional** (best-effort, classifier exception → `Inconclusive` verdict, no abort):
  `RunClassifier`

Every effect failure is logged as a `DecisionTransition`.

## L.18 Implementation Discipline

- **R1** Pre-flight plan check before every work step
- **R2** Delegate self-contained sub-tasks **and exploration** to subagents (Anthropic
  recommendation: preserve main-context window on large refactors — quality assurance)
- **R3** Real verification (DB round-trip, index dual-write, agent signal emission) — no
  "it compiles and runs"
- **R4** The plan is authority — deviations require user sign-off
- **R5** Out-of-scope items land in `tasks/todo.md`, not in memory
- **R6** High test coverage is a prerequisite for future refactorability, not a nice-to-have
- **R7** Code-structure discipline: small files (guideline ≤300 LoC), clear folders, partial
  classes by scenario, check in with the user on any uncertainty (placement, naming, scope)

---
