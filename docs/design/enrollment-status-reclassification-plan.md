# Implementation Plan — Enrollment Status Reclassification

> Working copy lives in `tasks/todo.md` (gitignored); this is the tracked, buildable checklist.

Design note: [`docs/design/enrollment-status-reclassification.md`](./enrollment-status-reclassification.md)
Branch: `claude/crcins-enrollment-failures-ac943m`

Goal: stop the 5h sweep from labelling silent-but-provisioned sessions `Failed`; add a
third terminal state `Incomplete/Unknown`; reconcile late completions to `Succeeded`.
Backend + stats only — no agent changes, no heartbeat.

---

## PR1 — Status model + ESP rollup extraction (foundation) ✅ done

- [x] Add `AwaitingUser` and `Incomplete` to `SessionStatus` enum
      (`SessionApiModels.cs`, appended — existing ordinals unchanged).
- [x] Extend `FailureSnapshotBuilder` to embed the ESP rollup
      (`deviceSetupAllSucceeded` / `accountSetup{SucceededCount,Total,AllSucceeded}`),
      schema v2. Gated on the subcategory rollup, NOT `categorySucceeded`.
- [x] Pure `EnrollmentTimeoutClassifier.ExtractRollup` + `ClassifyTimedOutSession`
      (new `EnrollmentTimeoutClassifier.cs`) implementing the decision table.
- [x] Unit tests `EnrollmentTimeoutClassifierTests` (12) — rollup extraction (0/5, 1–4/5,
      5/5, fallback, missing) + every classification branch. **Full Functions.Tests: 2670 pass.**

## PR2 — Sweep uses the classifier + grace window ✅ done

- [x] `TenantConfiguration.SessionGraceHours` (default 72) + default-config plumbing.
- [x] `MarkStalledSessionsAsTimedOutAsync` (Stage 2): replaced hard-coded `Failed` with
      `EnrollmentTimeoutClassifier.ClassifyTimedOutSession(...)`. Fast-path skips within-grace
      `AwaitingUser` sessions with NO event read (grace is time-based). Per-outcome counters +
      audit/ops only when the pass did something.
- [x] Synthetic `session_timeout` event + analyze enqueue kept ONLY for `Failed`/`Incomplete`
      terminal transitions (not `AwaitingUser`/`Succeeded`).
- [x] `GetStalledSessionsAsync` now includes `AwaitingUser` so grace→`Incomplete` fires later.
      `GetAgentSilentSessionsAsync` left InProgress-only (no regress).
- [x] `UpdateSessionStatusAsync`: `Incomplete` treated as terminal (idempotency guard);
      `AwaitingUser` never regresses a terminal; `CompletedAt` stamped for `Incomplete` (no
      duration); reason + snapshot persisted for `Incomplete`/`AwaitingUser`.
- [x] Decision logic + grace boundary covered by `EnrollmentTimeoutClassifierTests` (pure —
      the sweep itself has ~17 deps, same rationale as `MaintenanceServiceSessionTimeoutEventTests`).
      **Full Functions.Tests: 2670 pass.** End-to-end validated via the live forward-only watch.

## PR-EB — Agent emergency-break event (transparency: close the silent-48h blind spot)

The agent's 48h absolute session-age break (`Program.Guards.CheckSessionAgeEmergencyBreak`)
writes only a LOCAL marker + cleans up + exits — silent to the backend. Make it visible in the
timeline AND let the backend terminalize precisely instead of guessing with grace.

- [x] `Constants.EventTypes.AgentEmergencyBreak` + `AgentErrorType.SessionAgeEmergencyBreak`.
- [x] Classifier consumes it: `ExtractRollup.HasAgentEmergencyBreak`; a session carrying the break
      classifies as `Incomplete` NOW (skips the AwaitingUser grace) unless it actually completed.
      Tested. **This is the load-bearing backend consumer.**
- [x] **Part B (backend): materialize the timeline event.** `ReportAgentErrorFunction` now injects
      `ISessionRepository` and, on `SessionAgeEmergencyBreak`, writes an `agent_emergency_break`
      `EnrollmentEvent` (Sequence = max+1, idempotent, best-effort) into the stream. Pure
      `BuildAgentEmergencyBreakEvent` helper (Warning, non-terminal) + tests. Suite: 2685 pass.
- [x] **Part C (agent, net48 — CI/local build only): best-effort emit.** Implemented via a decoupled
      callback so the guard stays pure and its unit tests are untouched:
      - `CheckSessionAgeEmergencyBreak` gains an optional `Action onBreakFired = null`, invoked once when
        the break is confirmed, BEFORE the marker/cleanup, fully swallowed.
      - New `EmergencyBreakReporter.TrySend` builds a throwaway auth bundle (`BackendClientFactory`) and
        fires a `SessionAgeEmergencyBreak` `AgentErrorReport` over the resilient emergency channel, bounded
        to a 5s wait. `AgentBootstrap` wires it (`GetAgentVersion` made `internal`).
      - Lost if the device is fully offline — acceptable (that case falls back to the grace window).
      ⚠️ **Not compiled here** (agent targets net48; this container has only the .NET 8 SDK). The Shared
      `AgentErrorType.SessionAgeEmergencyBreak` value IS verified (it compiled in the Functions build).
      **@okieselbach to run the net48 agent build locally.**

## PR3 — Reconciliation of late completions ✅ done

- [x] `UpdateSessionStatusAsync` terminal-guard replaced by pure `IsTerminalTransitionAllowed`:
      a genuine completion (`Succeeded`) UPGRADES a prior `Failed`/`Incomplete`/`AwaitingUser`
      verdict (late reconcile); silent-terminal verdicts + `AwaitingUser` never overwrite a terminal.
- [x] On `Succeeded`, clear stale `FailureReason` + `FailureSnapshotJson` (reconcile hygiene).
- [x] Admin-marked terminal decisions are preserved — a late completion does NOT auto-override a
      hand-marked `Failed`/`Incomplete`.
- [x] Ingest already routes late `enrollment_complete`/`gather` → `Succeeded` with no pre-terminal
      skip, so the relaxed guard makes reconcile work end-to-end (no ingest change needed).
- [x] Tests `SessionStatusTransitionTests` (20, full matrix). Suite: 2705 pass (one pre-existing
      timing-flaky maintenance test, green on retry/isolation — unrelated).

## PR4 — Stats / metrics expose 3 states ✅ done (Fleet Health deferred to PR5)

- [x] `SessionStatusBuckets` (`MetricsMath.cs`): explicit `AwaitingUser` + `Incomplete` buckets
      (out of `Other`); buckets still reconcile to Total by construction.
- [x] Metrics summary (`GetMetricsSummaryAsync`): surface `awaitingUser` + `incomplete`; **failure
      rate now `Failed / (Succeeded + Failed)`** — Incomplete + non-terminal excluded from the denominator.
- [x] `SessionStats` (`SessionApiModels.cs`): add `IncompleteLastNDays`; dashboard `SuccessRatePct`
      was already terminal-only (`Succeeded/(Succeeded+Failed)`), so Incomplete is excluded there too.
- [x] Daily aggregation (`ComputeUsageMetricsSnapshotAsync`) already uses `Succeeded+Failed` as the
      denominator — no change needed (Incomplete already excluded).
- [x] Tests `SessionStatusBucketsTests` (bucketing + terminal-only failure-rate). Suite: 2705 pass.
- [ ] **Deferred to PR5:** Fleet Health payload Incomplete count (its SuccessRate is documented as
      "over all sessions" with equivalence tests — fold the Incomplete surfacing in with the web work).

## PR5 — Web surface ✅ done (typecheck clean)

- [x] `utils/sessionStatus.ts`: `isTerminalStatus` includes `Incomplete`; docstring notes AwaitingUser non-terminal.
- [x] `SessionStatusBadge.tsx`: `Incomplete` = neutral slate (clearly not red), `AwaitingUser` = sky/blue
      ("still going"); ⏱️ affordance extended to Incomplete (the silence family).
- [x] Fleet Health: `FleetHealthStats.Incomplete` (C# model + `MetricsMath.BuildFleetHealthPayload`) +
      web interface + a dedicated slate "Incomplete" stat card (`FleetStatCard` gained a `slate` color);
      grid widened to 5.
- [x] Main dashboard KPI row left as-is (its Success Rate is already the honest terminal rate); the third
      state is surfaced via the badge everywhere, the Fleet Health card, and the metrics summary `incomplete`.
- [x] Verified: `tsc --noEmit` clean; backend suite 2709 pass.

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

## Decisions (2026-07-08)
- **Third state label:** `Incomplete` (enum member `SessionStatus.Incomplete`, badge "Incomplete").
- **Scope:** forward-only first (PR1–PR5) to watch how the first live sessions classify.
  **PR6 backfill is deferred** — run it once forward-only looks good, to heal historical
  crcins.com + platform stats. Remembered follow-up.
- **`SessionGraceHours`:** now **auto-derived** from the agent's absolute cap, not a magic 72h.
  `effectiveGrace = max(AbsoluteMaxSessionHours + 12h buffer, override)` = **60h** by default
  (`EnrollmentTimeoutClassifier.ResolveGraceHours`). Rationale: the 48h agent emergency break is
  *silent* to the backend, so grace must be ≥ that cap and only slightly beyond it. `SessionGraceHours=0`
  means auto; an override can only raise the floor. `AbsoluteMaxSessionHours` mirrored into TenantConfiguration.
  - **Follow-up:** wire `TenantConfiguration.AbsoluteMaxSessionHours` down into `AgentConfigResponse` +
    RemoteConfigMerger so a tenant override reaches the agent too (today the agent still uses its own default 48).
