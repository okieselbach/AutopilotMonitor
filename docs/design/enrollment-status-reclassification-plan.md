# Implementation Plan — Enrollment Status Reclassification

> Working copy lives in `tasks/todo.md` (gitignored); this is the tracked, buildable checklist.

Design note: [`docs/design/enrollment-status-reclassification.md`](../docs/design/enrollment-status-reclassification.md)
Branch: `claude/crcins-enrollment-failures-ac943m`

Goal: stop the 5h sweep from labelling silent-but-provisioned sessions `Failed`; add a
third terminal state `Incomplete/Unknown`; reconcile late completions to `Succeeded`.
Backend + stats only — no agent changes, no heartbeat.

---

## PR1 — Status model + ESP rollup extraction (foundation)

- [ ] Add `AwaitingUser` and `Incomplete` to `SessionStatus` enum
      (`src/Shared/AutopilotMonitor.Shared/Models/SessionApiModels.cs:426`).
      Keep ordinal order stable (append; do not renumber existing members).
- [ ] Extend `FailureSnapshotBuilder` to parse `esp_provisioning_status` events and expose
      the **last** DeviceSetup and AccountSetup rollups: `{ deviceSetupAllSucceeded: bool,
      accountSetupSucceededCount: int, accountSetupTotal: int, accountSetupAllSucceeded: bool }`.
      Gate on the subcategory rollup, NOT `categorySucceeded` (always `"in_progress"`).
- [ ] Extract a pure `ClassifyTimedOutSession(...)` helper (static, unit-testable — mirror
      the existing `BuildSessionTimeoutEvent` / `DecideAutoAction` pattern in
      `MaintenanceService.cs`) implementing the decision table (§2 of the design note).
- [ ] Unit tests: rollup extraction (0/5, 1–4/5, 5/5, missing) + every classification branch,
      using fixtures under `tests/fixtures/` (e.g. `account-setup-provisioning-complete-v1.json`).

## PR2 — Sweep uses the classifier + grace window

- [ ] `TenantConfiguration.SessionGraceHours` (default 72) + config plumbing.
- [ ] In `MarkStalledSessionsAsTimedOutAsync` (Stage 2, `MaintenanceService.cs:~360`):
      replace the hard-coded `SessionStatus.Failed` with `ClassifyTimedOutSession(...)`.
      - 5h reached, DeviceSetup succeeded, within grace → `AwaitingUser` (non-terminal).
      - grace expired → `Incomplete`.
      - AccountSetup all-succeeded / `enrollment_complete` seen → `Succeeded`.
      - explicit failure event → `Failed` (unchanged).
- [ ] Keep the synthetic `session_timeout` event + analyze enqueue ONLY for the real
      `Failed`/`Incomplete` terminal transitions (not for `AwaitingUser`).
- [ ] `GetStalledSessionsAsync` / `GetAgentSilentSessionsAsync` must re-scan `AwaitingUser`
      so the grace→`Incomplete` transition fires on a later pass.
- [ ] Tests: `SessionTimeoutRuleTests` / `MaintenanceServiceSessionTimeoutEventTests` extended
      for the new target states + grace boundary.

## PR3 — Reconciliation of late completions

- [ ] Allow `Failed` / `Incomplete` / `AwaitingUser` → `Succeeded` when a genuine terminal
      arrives (`enrollment_complete`, `whiteglove_complete`, AccountSetup all-succeeded).
      Audit the `UpdateSessionStatusAsync` terminal-guard so ONLY these reconcile signals may
      upgrade an already-terminal row (no other Failed→X path opens up).
- [ ] Verify `EventIngestProcessor.Classification.cs` routes a late `enrollment_complete`
      through this path (it already sets `Succeeded`; confirm the guard).
- [ ] Tests: `SessionCompletionTimestampTests` + new reconcile test (sweep→Failed, then late
      `enrollment_complete` → Succeeded).

## PR4 — Stats / metrics expose 3 states

- [ ] `SessionStatusBuckets` (`MetricsMath.cs:317`): explicit `AwaitingUser` + `Incomplete`
      buckets (out of `Other`).
- [ ] `SessionStats` (`SessionApiModels.cs:442`): add `IncompleteLastNDays`; failure-rate
      denominator = `Succeeded + Failed` only.
- [ ] Fleet Health payload (`MetricsMath.BuildFleetHealthPayload`): count Incomplete
      separately; keep SuccessRate honest.
- [ ] Aggregation equivalence tests (`SessionStatsAggregationTests`,
      `SessionStatsProjectionEquivalenceTests`, `MetricsSummaryProjectionEquivalenceTests`).

## PR5 — Web surface

- [ ] Mirror enum + `isTerminalStatus` (`utils/sessionStatus.ts`): `Incomplete` terminal,
      `AwaitingUser` non-terminal.
- [ ] `SessionStatusBadge.tsx` colours/labels for both new states.
- [ ] Dashboard / Fleet Health cards render the third bucket; failure-rate copy updated.
- [ ] `lib/__tests__/sessionStatus.test.ts` updated.

## PR6 — Backfill (optional, one-shot)

- [ ] Manual maintenance path to re-classify historical `Failed` 5h-timeouts using the same
      classifier, so existing stats (crcins.com + platform) heal retroactively.
- [ ] Dry-run counters logged before any write; gated behind an explicit admin trigger.

---

## Verification
- [ ] `dotnet test` green across Functions.Tests + Shared/DecisionCore tests.
- [ ] Re-run the crcins.com window (via MCP `query_raw_events` / `search_sessions`) and confirm
      the "after" table in the design note within rounding (Failed ~122, Incomplete ~115,
      +25 reconciled, failure rate ~4–5 %).
- [ ] Confirm no `AwaitingUser` session ever emits a `session_timeout`/analyze `Failed` artifact.

## Review (fill in after implementation)
- _pending_

## Open questions for @okieselbach
- [ ] `SessionGraceHours` default — 72h ok, or tie to `SessionTimeoutHours` multiple?
- [ ] Reporting label for the third state: `Incomplete`, `Unknown`, or `Incomplete/Unknown`?
- [ ] Backfill (PR6) in scope now, or ship forward-only first and backfill later?
