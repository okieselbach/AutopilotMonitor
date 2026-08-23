---
type: concept
title: Diagnostics Package
description: What the agent's diagnostics ZIP contains and why — the built-in section catalog shared with the portal, the RealmJoin and Device Preparation collection gates, the configured-path guards, caps, and the package manifest.
resource: agent
tags: [diagnostics, realmjoin, device-preparation, guardrails, portal]
timestamp: 2026-08-23
---

# Schema

The diagnostics package is the agent's last-resort support artifact: a ZIP built in memory
by `DiagnosticsPackageService.BuildArchiveBytes` and uploaded through a short-lived SAS
URL at the end of a session, at the WhiteGlove seal (`Always` mode) or on a server-requested
on-demand collection ([server actions](../backend/server-actions-on-demand-diagnostics.md)).

## Archive layout

```
sessioninfo.txt                 session/tenant/device/outcome, hardware via WMI
<built-in sections>             catalog order, see below
AdditionalLogs/<last folder>/   admin-configured paths (global ∪ tenant)
package-manifest.txt            every packaging decision (always written)
_TRUNCATED.txt                  only when a cap skipped something
```

## The built-in catalog is the single source of truth

Everything the package collects **before** any configured path is data, not code paths:
`DiagnosticsBuiltInSections.All` in Shared (`Models/Diagnostics/DiagnosticsBuiltInSections.cs`).
The agent iterates it in order; the backend serves the same list to the portal
(`GET /api/diagnostics/paths`, MemberRead) so what administrators see is exactly what the
agent does. There is no second copy to drift.

| Id | ZIP folder | Source (unexpanded) | Patterns | Recursive | Condition |
| --- | --- | --- | --- | --- | --- |
| AgentLogs | `AgentLogs` | `%ProgramData%\AutopilotMonitor\Logs` | log patterns | no | Always |
| ImeLogs | `ImeLogs` | `%ProgramData%\Microsoft\IntuneManagementExtension\Logs` | log patterns | no | Always |
| ImeBootstrapperEventLog | `ImeLogs` | `C:\Windows\System32\winevt\Logs` | `BootstrapperAgentServiceLogProvider.evtx` | no | DevicePreparation |
| AgentState | `AgentState` | `%ProgramData%\AutopilotMonitor\State` | log patterns + `*.complete`, `*.marker` | yes | Always |
| AgentSpool | `AgentSpool` | `%ProgramData%\AutopilotMonitor\Spool` | `*.jsonl`, `*.json` | no | Always |
| AgentMarkers | `AgentMarkers` | `%ProgramData%\AutopilotMonitor` | `*.complete`, `*.marker` | no | Always |
| RealmJoinWindows | `RealmJoinLogs/Windows` | `C:\Windows\Logs` | `realmjoin*.log` | no | RealmJoinWatcher |
| RealmJoinPackages | `RealmJoinLogs/Windows/RealmJoin` | `C:\Windows\Logs\RealmJoin` | `*.log` | yes | RealmJoinWatcher |
| RealmJoinChoco | `RealmJoinLogs/Choco` | `%ProgramData%\RealmJoin\choco\logs` | `*.log` | yes | RealmJoinWatcher |
| RealmJoinUserTray | `RealmJoinLogs/User` | `%LOGGED_ON_USER_PROFILE%\AppData\Local\RealmJoin` | `tray*.log` | no | RealmJoinWatcher |
| RealmJoinUserLogs | `RealmJoinLogs/User/Logs` | `%LOGGED_ON_USER_PROFILE%\AppData\Local\RealmJoin\Logs` | `*.log` | yes | RealmJoinWatcher |

"Log patterns" = `*.log *.txt *.json *.jsonl *.etl *.evtx *.xml *.csv *.cab`. The five
`Always` sections are the historical layout (PR1-B forensics) and are lock-tested in order;
the RealmJoin ZIP layout mirrors the disk (`C:\Windows\Logs\{realmjoin*.log, RealmJoin\…}` →
`RealmJoinLogs/Windows/…`, `%LOCALAPPDATA%\RealmJoin\{tray*.log, Logs\…}` →
`RealmJoinLogs/User/…`), so a flat section and a tree section never produce the same entry
name. Two sections may share a ZIP folder only when they read different source folders
(`ImeLogs` + the bootstrapper evtx) — `(ZipFolder, SourceFolder)` pairs are unique.

### Conditions

`DiagnosticsSectionCondition` is evaluated by the agent (`IsSectionActive`, pure) against its
effective configuration and the enrollment scenario; unknown values fail closed.

* **RealmJoinWatcher** — the tenant's RealmJoin Watcher toggle
  (`AnalyzerConfiguration.EnableRealmJoinWatcher`). The package service only sees
  `AgentConfiguration`, so `RemoteConfigMerger` mirrors that one Analyzers knob onto
  `AgentConfiguration.EnableRealmJoinWatcher` (the RealmJoin host keeps reading the remote
  block). Deliberately NOT additionally gated on the compile-time `RealmJoinTrackingEnabled`
  kill switch: if the live watcher is ever disabled, the logs become more useful, not less.
  No ConfigVersion bump — no new wire field.
* **DevicePreparation** — `EnrollmentRegistryDetector.IsDeterministicDevicePreparation()`,
  the same deterministic `DevicePreparation\BootstrapperAgent\ExecutionContext` marker every
  other WDP gate keys on ([WDP scenario gates](wdp-scenario-gates.md)); CloudAssigned*
  fallback rules count as Classic. The probe is a registry read, evaluated once per build
  (`SCENARIO: devicePreparation=… realmJoinWatcher=…` in the manifest); a probe failure is
  treated as Classic and logged — the evtx export is skipped, never the package. The channel
  is exclusively locked while active, so the existing `wevtutil epl` export path
  (`ResolveChannelForEvtxFile`, field case 2026-08-17) handles it.

Inactive sections are never silent: `BUILT-IN SKIPPED (RealmJoin Watcher disabled): <Id>`,
`BUILT-IN SKIPPED (not a Device Preparation enrollment): <Id>`,
`BUILT-IN SKIPPED (no user session for token): <Id> '<source>'` (the
`%LOGGED_ON_USER_PROFILE%` token resolves via `UserProfileResolver` only once an
interactive user exists — identical to configured paths). Active ones log
`BUILT-IN: <Id> -> folder='…' zip='…' recursive=…` followed by the per-pattern
`ADDED / NO MATCH / FOLDER MISSING` lines.

## Built-ins bypass the configured-path guards — on purpose

`DiagnosticsPathGuards` ([gather-rule guardrails](../rules/gather-rule-guardrails.md))
validates **admin-typed** paths: allowlisted prefixes, wildcard only in the last segment,
`C:\Users` blocked except the token's `AppData\Local|Roaming`. The catalog is reviewed code,
exactly like the five sections that never went through the guard before — and
`%ProgramData%\RealmJoin` is not on the allowlist at all. What does apply to every section:
the reparse-point skip (files and directories), the `.evtx` channel export, and the caps
(100 MB per file, 500 MB total, 5000 files) tracked globally across sections by
`BudgetTracker`, with every skip recorded in the manifest and `_TRUNCATED.txt`.

## Configured paths: global ∪ tenant

`GetAgentConfigFunction.MergeDiagnosticsLogPaths` sends global paths (Global Admin,
`AdminConfiguration.DiagnosticsGlobalLogPathsJson`) first, then tenant paths
(`TenantConfiguration.DiagnosticsLogPathsJson`), dropping blank entries and collapsing
duplicates (trimmed, case-insensitive) onto the first occurrence — a path present in both
lists used to produce duplicate ZIP entry names. The agent keys each entry's ZIP folder on
the last directory name (`AdditionalLogs/<folder>`), so two configured paths ending in a
same-named folder share one ZIP folder.

## Portal surfaces

* Global Admin → Settings → Diagnostics Log Paths: the built-in catalog (collapsed, read-only,
  neutral condition pills) above the editable global list; add-row first.
* Tenant → Settings → Agent → Diagnostics Package: built-in catalog (header carries the
  tenant's persisted RealmJoin Watcher state, RealmJoin rows show on/off), the global paths
  read-only, then the tenant's own entries; every role with a tenant membership can read the
  catalog (the route is MemberRead, JWT-scoped). One line per entry (`DiagnosticsPathRow`),
  full values in tooltips.

## Test seams

* `DiagnosticsPackageService` internal ctor: the five historical folder overrides map onto
  catalog ids; `sectionFolderOverrides` covers any other section; `devicePreparationProbe`
  replaces the registry read (seam callers default to "not WDP", so existing rigs never touch
  the registry). `UserProfileResolver.SetForTesting(path|null)` / `Reset()` drive the token.
* Catalog invariants live in the backend test project (`DiagnosticsBuiltInSectionsTests`),
  because Shared has no test project.

# Examples

A RealmJoin tenant (watcher on) with a signed-in user: `RealmJoinLogs/Windows/realmjoin.log`,
`RealmJoinLogs/Windows/RealmJoin/Packages/<pkg>/<timestamp>_install.log`,
`RealmJoinLogs/Choco/<pkg>/<timestamp>_install.log`, `RealmJoinLogs/User/tray.log`,
`RealmJoinLogs/User/Logs/RjImeHost.log`. The same tenant's package collected before the
desktop: the two `User` sections appear as `BUILT-IN SKIPPED (no user session for token)`.
A Classic enrollment: `BUILT-IN SKIPPED (not a Device Preparation enrollment):
ImeBootstrapperEventLog`. A tenant that had added
`%LOGGED_ON_USER_PROFILE%\AppData\Local\RealmJoin\Logs\*.log` manually keeps working — the
files simply appear twice (`AdditionalLogs/Logs/…` and `RealmJoinLogs/User/Logs/…`) until the
entry is removed.

# Citations

* `src/Shared/AutopilotMonitor.Shared/Models/Diagnostics/DiagnosticsBuiltInSections.cs` — the catalog
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Runtime/DiagnosticsPackageService.cs` — `BuildArchiveBytes`, `AddBuiltInSection`, `IsSectionActive`, caps, evtx export
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Configuration/RemoteConfigMerger.cs` — `EnableRealmJoinWatcher` mirror
* `src/Backend/AutopilotMonitor.Functions/Functions/Diagnostics/GetDiagnosticsPathsFunction.cs` — `GET diagnostics/paths`
* `src/Backend/AutopilotMonitor.Functions/Functions/Config/GetAgentConfigFunction.cs` — `MergeDiagnosticsLogPaths`
* `src/Web/autopilot-monitor-web/components/diagnostics/` — `DiagnosticsPathRow`, `BuiltInSectionsList`, `builtInSectionDisplay`
* Tests: `DiagnosticsPackageServiceTests` (RealmJoin on/off, no user session, bootstrapper evtx, probe failure), `DiagnosticsBuiltInSectionsTests`, `GetDiagnosticsPathsPayloadTests`, `GetAgentConfigFunctionTests.MergeDiagnosticsLogPaths_*`
