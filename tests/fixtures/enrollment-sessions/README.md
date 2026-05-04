# Enrollment Session Fixtures — committed, anonymized

Each `.jsonl` file in this directory is an anonymized DecisionSignal fixture used by
the Replay Harness and the reducer scenario tests (plan §4 M3 gate + §R6 test
discipline).

## Commit policy

A fixture may only land here when **all** of the following hold:

1. **Anonymized.** No tenant IDs, device IDs, serial numbers, user names, UPN prefixes,
   or raw IP addresses. Use the placeholder tokens `tenant-anon-NNNN`, `device-anon-NNNN`,
   `user-anon-NNNN`. Identifiers are consistent within a fixture but never reused across
   fixtures.
2. **Pflicht-Evidence.** Every signal has non-empty `Evidence.Identifier` and `Summary`.
   `Derived` signals carry non-empty `DerivationInputs`. `Synthetic` signals carry a
   correlation ID. Plan §2.2 signal adapter rules apply.
3. **Category tag.** Filename prefix marks the scenario category:
   - `userdriven-happy-*` — ESP → AccountSetup → Hello → Desktop → Complete
   - `userdriven-hello-timeout-*`
   - `selfdeploying-*`
   - `whiteglove-inline-*`
   - `whiteglove-signal-correlated-*`
   - `whiteglove-false-positive-*`
   - `whiteglove-antiloop-*`
   - `hybrid-reboot-*`
   - `esp-terminal-failure-*`
   - `hello-timeout-*`
   - `late-aadj-*`
4. **README entry.** Add a line to the `Fixtures` table below with the expected terminal
   state + hypotheses.
5. **Pair with scenario test.** At least one xUnit test in
   `src/Agent/AutopilotMonitor.DecisionCore.Tests/Scenarios/` consumes the fixture and
   asserts its terminal state (plan R6 flow-test priority).

## Fixtures

Authoritative terminal-state expectations live in the paired scenario tests, not in this
table — see [`AutopilotMonitor.Agent.V2.Core.Tests/Integration/ScenarioTests.cs`](../../../src/Agent/AutopilotMonitor.Agent.V2.Core.Tests/Integration/ScenarioTests.cs)
and [`AutopilotMonitor.DecisionCore.Tests/Scenarios/`](../../../src/Agent/AutopilotMonitor.DecisionCore.Tests/Scenarios/).
Add a row below for any new fixture so reviewers can spot coverage gaps at a glance.

| Filename | Category | Terminal Stage | EnrollmentType | Notes |
|---|---|---|---|---|
| `userdriven-hello-skipped-v2.jsonl` | userdriven-happy (variant) | Completed | v1 | Real-world anon replay of session df7abbc2. HelloOutcome=skipped (not Success); ScenarioProfile.Mode=Classic via ImeUserSessionCompleted. 23 decision signals. |

## Ordering

Fixtures preserve the capture `SessionSignalOrdinal`. If you edit a fixture (e.g., remove
a noisy signal), renumber ordinals contiguous from 0 and re-hash the harness output —
then update the paired scenario test's expected hash.
