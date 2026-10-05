# OOBE update interval and live label — shared cases

While a session runs, the portal shows the OOBE quality update live; once the session ended,
the time attribution shows it as its `os_update` and `awaiting_sign_in` segments. Both read the
same interval, and `cases.json` is the one definition that every implementation runs:

- Backend — the update interval of `TimeAttributionCalculator` in
  `src/Backend/AutopilotMonitor.Functions/Helpers/TimeAttributionCalculator.cs`
  (test: `TimeAttributionOsUpdateLiveParityTests.cs`)
- Web — `computeOsUpdateInterval` and the live label (`deriveOsUpdateLive`, `osUpdateLiveState`)
  in `src/Web/autopilot-monitor-web/lib/osUpdateLive.ts` (test: `lib/__tests__/osUpdateLive.test.ts`)

A scenario is a session (`startedAt`; for WhiteGlove also `resumedAt` and `isPreProvisioned`) and
its events, listed in the order the agent sent them, which is their sequence. `t` is the event
time. `arrives` marks an event that reached the backend later than it happened: a backfill, or a
restart the agent reported after the boot. `phase` is an `EnrollmentPhase` name.

A cut is the session at `now`, with the events that had arrived before it:

- `interval` — what the time attribution reports for the current part (from `resumedAt`, else
  `startedAt`) if the session had ended at `now`: begin, end, restarts, outcome and KBs. It is
  null when there is no update. The backend computes it through `TimeAttributionCalculator.Compute`.
- `live` — the label the portal shows at `now`: `updating` (with `restarting` while a restart is
  pending), `awaiting_sign_in` or `ended`. It is null when the portal shows none. Only the web
  reads it.

A change to the update logic goes into `cases.json` first; the two tests then say which side is
behind. The calculator computed the expected intervals and each one was checked by hand; the
labels are written by hand.
