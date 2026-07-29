---
type: Concept
title: Gather Rule Debug Log — the per-tenant evaluation trace for rules that produce nothing
description: EnableGatherRuleDebugLog (ConfigVersion 36) makes the agent write a local trace file explaining every gather-rule evaluation decision — scope skips, missing intervalSeconds, on_change suppression, empty collector results, logparser position/parse-mode/no-match outcomes — so customers can self-diagnose rules that never show up in the timeline.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleDebugLog.cs
tags:
  - agent
  - gather-rules
  - diagnostics
  - tenant-configuration
timestamp: 2026-07-28T00:00:00+02:00
---

# Gather Rule Debug Log

## Schema

### The problem

A gather rule that produces no timeline events is indistinguishable from a rule that
never ran. The pipeline has many *silent-nothing* exits — before this feature, none of
them were visible to the customer:

| # | Decision point | Where |
|---|---|---|
| 1 | Rule delivered but `Enabled=false` | `GatherRuleExecutor.UpdateRules` |
| 2 | `trigger=interval` without `intervalSeconds` — never runs | `UpdateRules` timer filter |
| 3 | Interval timer's first tick comes only after one **full** interval | `UpdateRules` timer creation |
| 4 | Out of phase scope (`activePhases` / `activeFromPhase` not latched); incl. `currentPhase=Unknown` before the first phase signal | `IsRuleInScopeLocked` |
| 5 | phase_change / phase_exit dedup — once per (rule, phase) | `OnPhaseChanged` |
| 6 | Collector returned an **empty** result (e.g. `emitOnlyIfExists=true` and target absent) | `ExecuteRule` |
| 7 | `emit_mode=on_change` — result hash unchanged, emit suppressed | `ShouldEmitOnChange` |
| 8 | Guard blocked the target (emits `security_warning`, but the rule yields no data) | `GatherRuleContext.EmitSecurityWarning` |
| 9 | logparser: no files matched, position tracker says nothing new, regex no-match, plain-text file parsed in default `cmtrace` mode, `maxLines` cap, wildcard capped at 20 files | `LogParserCollector` |
| 10 | Rule execution threw (agent log had only the message, no stack) | `ExecuteRule` catch |

### The setting

`TenantConfiguration.EnableGatherRuleDebugLog` (`bool?`, default null→false), served to the
agent as `AgentConfigResponse.EnableGatherRuleDebugLog` — **ConfigVersion 36**. Portal:
Settings → Agent → Settings, toggle "Gather Rule Debug Log", stored via the usual
tenant-config roundtrip (`TableConfigRepository` Store+Map). Also shown in the admin
tenant-config report.

`RemoteConfigMerger` projects the bool onto a runtime path (same pattern as
`EnableImeMatchLog`): `true` → `Constants.GatherRuleDebugLogPath` =
`%ProgramData%\AutopilotMonitor\Logs\gather_rules_debug.log`; `false` → null, unless a
`--gather-debug-log <path>` CLI override supplied a non-default path. The field is not
security-sensitive and deliberately **survives the offline config cache**.

### The trace file

`GatherRuleDebugLog` is an append-only writer held by `GatherRuleExecutor` and exposed
to collectors via `GatherRuleContext.DebugLog(ruleId, stage, message)` (null-safe no-op
when disabled — zero cost on the normal path). Line format:

```
2026-07-28T09:15:02.123Z | RULE-ID | stage | message
```

Stages: `config` (registration summary per rule, header, zero-rules note), `phase`
(enrollment-phase transition marker `phase change: Old -> New`, ruleId `-` — every
rule-level line below it reads against the new phase), `trigger` (timer scheduled,
phase/event fired, dedup), `scope` (skips with reason incl. current phase), `exec`
(execution start), `collector` (null/empty results with likely cause), `guard`
(allowlist block), `emit` (event emitted, hash change), `suppress` (on_change
suppression with hash prefix + streak), `logparser` (per-file outcome: lines read,
position range, match count, parse failures, regex timeouts, mode; plus the
`format=text` hint when every line fails CMTrace parsing; plus one line **per regex
match** — line number, matched text, capture groups — capped at 10 per file per run),
`error` (full exception **with stack trace** — the agent log keeps only the message;
an invalid logparser regex is reported here including the offending pattern string).

Volume control: 10 MB cap with a single `.old` rotation generation (~20 MB worst case).
Every write failure is swallowed — tracing must never break gathering. The trace never
leaves the device on its own — but because it lives in the agent log folder
(`Constants.LogDirectory`), the diagnostics package (`DiagnosticsPackageService`,
`*.log` pattern, `AgentLogs/` section) picks it up automatically, so a customer-initiated
diagnostics upload hands the trace to support without manual copying.

### Semantics

* **No hot-reload** — the flag is consumed once when `DefaultComponentFactory` builds
  the `GatherRuleExecutorHost` at session start (per-enrollment lifecycle, same as
  `EnableImeMatchLog`). Enable it in the tenant, then the *next* enrollment writes the
  trace.
* **Zero-rules case is traced too** — flag on + backend delivered no gather rules →
  a one-shot standalone `config` line, so the file is never just absent.
* **`--run-gather-rules` always traces** (operator diagnostic mode; CLI override or
  default path) and mirrors every line to the console under `--console`.

## Examples

A logparser rule with `emit_mode=on_change` that "delivers nothing" becomes:

```
... | LP-01 | config | registered: trigger=interval, collector=logparser, target=C:\...\app.log, unscoped, emitMode=on_change, interval=300s
... | LP-01 | trigger | interval timer scheduled every 300s (first run after one full interval — no immediate execution)
... | -     | phase | phase change: DeviceSetup -> AccountSetup
... | LP-01 | exec | executing: collector=logparser, target=C:\...\app.log
... | LP-01 | logparser | app.log: read 1000 lines from position 0->81234, matched 0, parseFailures=1000, regexTimeouts=0, mode=cmtrace
... | LP-01 | logparser | every line failed CMTrace parsing — if this is a plain-text log, set parameter format=text
```

— the answer ("plain-text log parsed in CMTrace mode") is in the file.

A matching rule shows *how* it matched (capped at 10 per-match lines per file per run):

```
... | LP-02 | logparser | setupact.log: line 42 matched "Error 0x80070005" — groups: code="0x80070005"
... | LP-02 | logparser | setupact.log: read 500 lines from position 0->40123, matched 1, parseFailures=0, regexTimeouts=0, mode=text
```

and a broken regex names itself:

```
... | LP-03 | error | invalid regex pattern '([unclosed' — rule can never match: Invalid pattern '([unclosed' at offset 10. Not enough )'s.
```

## Citations

* Executor + trace points: `../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleExecutor.cs`
* Writer: `../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleDebugLog.cs`
* Config projection: `../../src/Agent/AutopilotMonitor.Agent.V2.Core/Configuration/RemoteConfigMerger.cs`
* Backend serve + ConfigVersion: `../../src/Backend/AutopilotMonitor.Functions/Functions/Config/GetAgentConfigFunction.cs`
* Phase scoping / emit mode background: `../rules/gather-rule-phase-scoping.md`
