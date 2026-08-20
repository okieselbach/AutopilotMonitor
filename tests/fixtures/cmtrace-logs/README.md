# CMTrace Log Fixtures — committed, anonymized

Raw CMTrace log lines, kept verbatim, used by parser and offset-calibration unit tests in
`src/Agent/AutopilotMonitor.Agent.V2.Core.Tests/`.

## Why this is a separate corpus from `../enrollment-sessions/`

`enrollment-sessions/` holds anonymized **DecisionSignal** JSONL replayed through the Replay
Harness and the reducer scenario tests — signals that have already been parsed, adapted and
evidence-tagged. These files are the layer *below* that: unparsed device log text, the raw input
to `CmTraceLogParser`. They cannot satisfy that corpus's schema (no signal shape, no
`Evidence.Identifier`, no terminal state to assert) and its category prefixes do not apply.

Both live under `tests/fixtures/` so committed test data has one home. Test projects must not
carry their own fixture directories.

## Commit policy

1. **Anonymized.** No tenant IDs, device IDs, serial numbers, user names, UPN prefixes, raw IP
   addresses, or tenant-scoped object GUIDs (Intune policy/script IDs are tenant-scoped — replace
   them with `policy-anon-NNNN-0000-0000-0000-000000000000`). The all-zero GUID is a literal in
   IME's own output and stays. A session ID may stay when it is OUR diagnostic anchor and is
   already referenced from commit messages and `tasks/todo.md`.
2. **Verbatim otherwise.** Timestamps, spacing and the `<![LOG[...]]LOG]!>` envelope are the point
   of the fixture — the parser is being tested against real formatting, so nothing else is
   normalised or reflowed.
3. **Header comment.** State the source session, the agent version, and precisely which property
   the fixture exists to pin. A fixture whose purpose is not written down decays into noise.
4. **Versioned filename.** `-vN` suffix; never edit a committed fixture in place — a test pinned
   to it would silently change meaning.
5. **Extension `.cmtrace`,** not `.log`: `*.log` is globally gitignored.

## Fixtures

| File | Source | Pins |
|---|---|---|
| `agentexecutor-two-writer-eras-v1.cmtrace` | session `e9753578`, agent 2.0.1410, `ImeLogs/AgentExecutor.log` | One file containing TWO writer eras — 371 lines rendered at local hour 05 by a writer on PDT (UTC-7) and 195 at hour 14 by one already on CEST (UTC+2), nine hours apart, interleaved, no bias suffix on any line. The case that broke per-file offset calibration and forced revert `04b1a7c6`. Any era-aware calibrator must resolve BOTH halves correctly. |
