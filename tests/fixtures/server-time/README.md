# Server-time rule — shared cases

Device clocks are wrong in a measurable share of sessions: off by whole hours for the entire session
(time zone in the hardware clock), stepped mid-session by a program or a sync, or back on a different
era after a restart. Durations therefore come from server time, and `cases.json` is the one
definition that every implementation runs:

- Backend — `ServerTime` in `src/Backend/AutopilotMonitor.Functions/Helpers/ServerTime.cs`
  (test: `ServerTimeParityTests.cs`)
- Web — `lib/serverTime.ts` in `src/Web/autopilot-monitor-web` (test: `lib/__tests__/serverTime.test.ts`)

A case is one session: its events with the device event time `t`, the upload's device send time
`sent` (`X-Send-Time-Utc`, null for agents without it) and the server receive time `recv`, plus
`completedAt` (the stored device-clock completion, null while running) and `isPreProvisioned`.
Every event of one upload shares `sent` and `recv`.

The rule:

- **Offset of an upload** = `sent − recv` (device clock error minus upload latency). An upload with
  `agent_started` opens the next agent run. Per upload, the offset in use is the median of its own
  and the two previous uploads of the same run; the first two uploads of a run use the median of
  the run's first three. One delayed upload never moves it.
- **Start** = the earliest event time, as today (anchor-eligible sources, never more than 2 h before
  the first `agent_started`), minus the start offset (median of the first three uploads of run 1).
  Candidates come only from run 1 while its offset stays within 60 s of the start offset. An upload
  whose own offset is outside that band counts only once the next upload is back inside it — a late
  upload, not a clock step.
- **End** = the live activity event (`0 ≤ sent − t ≤ 60 s`, not a history watcher) at
  `completedAt`, else the last one before it, minus its offset.
- **Part 2 start** (`part2Start`) = the first `agent_started` after the first `whiteglove_complete`,
  minus the median of that run's first three uploads; reported whenever both exist, also while
  running and when Part 2 does not fit the duration.
- **WhiteGlove** (`isPreProvisioned`) — Part 1 = first `whiteglove_complete` − start; Part 2 =
  end − Part 2 start, applied only when the Part 2 start is not after the end.
- **Event server time** (`eventServerTimes`) = event time minus its upload's offset in use.

While a session runs, the backend keeps the rule's streaming form on the session row
(`ServerTime.Tracker`, column `ServerTimeState`): it absorbs one upload at a time and writes
`StartedAtServer` and `ResumedAtServer`. It follows a session only from its first upload, and only
when that upload carries `agent_started`; other sessions get no live values. `ServerTimeParityTests`
checks the tracker against the rule on every upload prefix of every followed case. Once `CompletedAt`
is set, the backend computes the rule once more over the session's stored events and writes
`StartedAtServer`, `ResumedAtServer` (the Part 2 start) and `CompletedAtServer`.

`expect` holds `start`, `startOffset` (seconds), `end`, `duration`, `part1`, `part2` (seconds),
`part2Start` and the probed event server times; null where the input does not allow a value (no
`sent`, still running). Expectations come from the reference implementation the rule was measured
with; every value must be reproduced to the millisecond. A change to the rule goes into `cases.json`
first; the two tests then say which side is behind.
