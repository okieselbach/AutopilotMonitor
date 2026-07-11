---
type: Component Overview
title: V2 Agent — Runtime Overview
description: How the V2 agent boots during Autopilot enrollment, which collectors it runs, how telemetry flows to the backend, and how it decides to stop.
resource: /src/Agent/AutopilotMonitor.Agent.V2/Program.cs
tags:
  - agent
  - runtime
  - collectors
  - telemetry
timestamp: 2026-07-11T00:00:00+02:00
---

# V2 Agent — Runtime Overview

The agent is a .NET Framework 4.8 executable (`AutopilotMonitor.Agent.exe`) that runs as
SYSTEM on Windows devices during Autopilot enrollment. It observes the enrollment
(ESP phases, IME app installs, Hello, desktop arrival, …), streams telemetry to the
backend, and decides locally — via the [decision engine](/agent/decision-engine.md) —
when the enrollment is complete or failed. When it reaches a terminal decision it shows
the optional summary dialog, uploads diagnostics, and removes itself.

Projects: `src/Agent/AutopilotMonitor.Agent.V2` (exe) + `AutopilotMonitor.Agent.V2.Core`
(library) + `src/Shared/AutopilotMonitor.DecisionCore` (portable decision engine).

# Lifecycle

## Install (bootstrap during OOBE)

`AutopilotMonitor.Agent.exe --install` is invoked by the Intune Platform Script
(bootstrap) and never runs the monitoring runtime
(`src/Agent/AutopilotMonitor.Agent.V2/Program.InstallMode.cs`):

1. Copy payload to `%ProgramData%\AutopilotMonitor\Agent\`.
2. Persist `bootstrap-config.json` (bootstrap token, tenant id) and `await-enrollment.json`.
3. Register the `AutopilotMonitor-Agent` scheduled task (BootTrigger, SYSTEM,
   `ExecutionTimeLimit=PT0S`, survives battery conditions).
4. Immediate runtime handoff: WMI `Win32_Process.Create` → `schtasks /Run` fallback →
   defer to BootTrigger on next reboot. `--install` exits 0 even if both immediate
   launches fail (fail-soft; the BootTrigger is the net).
5. Write `HKLM\SOFTWARE\AutopilotMonitor\Deployed` = UTC timestamp — the **bootstrap
   re-entry lock** read by the install script. Never consulted by the agent itself;
   intentionally preserved across cleanup.

## Runtime start (each process start / boot)

`Program.Main` → `RunAgent` runs numbered phases (V1-parity exit codes 0–7):

1. **Guards & bootstrap** (`AgentBootstrap.Run`): classify previous exit
   (`clean` / `exception_crash` / `hard_kill` / `reboot_kill` via `clean-exit.marker`,
   crash logs, boot event 6009); ghost-restart guard (`enrollment-complete.marker`
   → cleanup + exit 0); absolute session-age emergency break; optional waits
   (`--await-enrollment` MDM cert poll, `--tenant-id-wait` registry wait); resolve
   TenantId from `HKLM:\SOFTWARE\Microsoft\Enrollments`; get-or-create SessionId.
2. **Auth clients** (mTLS with the Intune MDM device client certificate).
3. **Remote config** (`GET /api/agent/config`): live fetch → cached
   `Config\remote-config.json` → built-in defaults. Config-channel **kill switch**
   checked here, honoured only from a *live* fetch (cache strips all security-sensitive
   fields).
4. **Telemetry clients**, **backend session registration**.
5. **`AgentRuntimeHost.Run`**: builds the `EnrollmentOrchestrator`, starts collector
   hosts, decision pipeline and upload drain loop, then blocks until shutdown.

## Stop triggers

- **Terminal decision** of the engine (Completed / Failed / WhiteGloveSealed) — the normal path.
- **Max-lifetime watchdog**: `AgentMaxLifetimeMinutes` (default 360) — hard cap per session.
- Backend **kill switch** (config channel at start; telemetry channel via upload responses
  → synthesized `terminate_session` action).
- Auth-failure threshold, self-update restart, Ctrl+C / ProcessExit.

Distinct from agent lifetime: `CollectorIdleTimeoutMinutes` (default 15) stops only the
periodic collectors (Performance, AgentSelfMetrics) when nothing "real" happens; they
restart on new activity.

## Termination sequence

On terminal decision (`EnrollmentTerminationHandler`): stop collectors → build
`FinalStatus` → write `final-status.json` + launch `SummaryDialog` (separate WPF exe in
the user session, opt-in) → 2 s late-event grace → diagnostics ZIP upload →
write `enrollment-complete.marker` → optional reboot → self-destruct cleanup
(skipped for WhiteGlove Part 1, which instead writes the `whiteglove.complete` marker
and leaves state for Part 2).

# Collector model

Collectors are wrapped in **collector hosts** (`ICollectorHost`, built by
`DefaultComponentFactory.CreateCollectorHosts`). Each host owns collector/tracker
classes plus a **signal adapter** that translates raw callbacks into decision signals.
Main hosts (gating in parentheses; several more are opt-in via remote config):

| Host | Watches |
| --- | --- |
| EspAndHelloHost | ESP phases, Shell-Core events, provisioning status, Hello (always) |
| ImeLogHost | IME log tailer (CMTrace) + IME process watcher — app installs (always) |
| DesktopArrivalHost | real-user desktop arrival (always) |
| AadJoinHost | AAD join events + hybrid login pending (always) |
| DeviceInfoHost | one-shot hardware/OS/network inventory (always) |
| StallProbeHost | idle/stall probes (config) |
| WindowsUpdateWatcherHost / OsBuildChangeHost | WU during OOBE, build changes (config / always) |
| DeliveryOptimizationHost | DO counters for bandwidth estimate (config, dormant until installs run) |
| OfficeInstallDetectorHost, ConsoleBypassHost, ProvisioningPackageHost, NetworkChangeHost, RealmJoinHost, GatherRuleExecutorHost, … | feature-specific (mixed gating) |

# Event pipeline (single rail)

Everything a collector observes flows through **one** path — no collector emits
telemetry directly:

```
Collector callback
  → SignalAdapter (dual emission: decision signal + InformationalEvent)
    → SignalIngress.Post()            (bounded queue 256, back-pressure blocks producer)
      → single worker thread:
          assign ordinals → SignalLog.Append (durable)   ← source of truth
          → DecisionEngine.Reduce → apply step/effects
          → telemetry projection (best-effort)
            → TelemetrySpool → batch (≤100) → gzip → POST /api/agent/telemetry
```

- Wire model: `EnrollmentEvent` (`EventType` from `Constants.EventTypes`, monotonic
  `Sequence` = canonical order, `Phase`, `ImmediateUpload` for lifecycle-critical items),
  wrapped in `TelemetryItem` (Kind = Event / Signal / DecisionTransition, monotonic
  `TelemetryItemId` as upload cursor).
- Auth per request: client-cert mTLS + hardware headers (`X-Tenant-Id`,
  `X-Device-Manufacturer/-Model/-SerialNumber`, `X-Bootstrap-Token` pre-MDM-cert,
  `X-Agent-Version`).
- Retry: 100/400/1600 ms for transient (408/429/5xx); 413 → split batch and halve batch
  size; unknown 4xx → items retained (cursor holds) unless the backend flags explicit
  poison row keys; offline → spool keeps everything, backend dedups on re-upload;
  `DeviceBlocked` pauses the drain until `UnblockAt`.

# Decision integration

Adapters post `DecisionSignal`s; the engine's terminal stage fires
`Terminated` (off the worker thread) → termination sequence above. Engine *effects*
(timeline events like `enrollment_complete`, deadline scheduling, classifier runs,
snapshots) are executed by the `EffectRunner` — the reducer itself is pure. Details:
[decision engine](/agent/decision-engine.md); durability and replay:
[logs & persistence](/agent/logs-and-persistence.md).

# On-disk layout (`%ProgramData%\AutopilotMonitor\`)

| Path | Contents |
| --- | --- |
| `\` | `bootstrap-config.json`, `await-enrollment.json`, `clean-exit.marker`, session-id persistence |
| `\Agent\` | deployed binaries |
| `\State\` | decision persistence (SignalLog, Journal, Snapshot), `enrollment-complete.marker`, `final-status.json`, self-update state, `.quarantine/`, `.part1-<ts>/` |
| `\Spool\` | telemetry spool (offline queue) |
| `\Logs\` | agent trace log, `crash_*.log`, `ime_pattern_matches.log` |
| `\Config\` | `remote-config.json` cache |
| `\Agent-Update\`, `\Updates\` | self-update staging/download |

Per-file details and the recovery flow: [logs & persistence](/agent/logs-and-persistence.md).
