---
type: concept
title: IME Process Watcher — Identity Before Attach, Re-Arm After Exit
description: Why ImeProcessWatcher attaches only to a process that runs in session 0 with its image under the IME install root — a process name is not an identity — and why a reported exit re-arms discovery instead of ending it.
resource: src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeProcessWatcher.cs
tags: [agent, ime, process, security, cwe-807, reliability]
timestamp: 2026-08-30
---

# Problem

`ImeProcessWatcher` reports `ime_process_exited` (Warning — read as an IME crash indicator in the
timeline) by subscribing to `Process.Exited` of `IntuneManagementExtension.exe`. Until 2026-08-30 it
attached to `Process.GetProcessesByName(...)[0]` and stopped discovery for good once attached.

A process name is not a protected namespace. During AccountSetup (and on Device Preparation flows)
a standard-user session is live and can start any binary renamed to `IntuneManagementExtension.exe`.
Two consequences (CWE-807 — the same class as the agent instance guard and the local-admin dynamic
allowance):

* **Forged signal** — the fake exits, the SYSTEM agent emits a Warning with the fake's PID/exit
  code, and failure diagnosis is misdirected.
* **Muted signal** — attached to the fake, discovery is stopped; the real service is never watched,
  and a genuine IME crash later in the enrollment is never reported. Even without an attacker the
  one-shot design missed every IME exit after the first (an IME service restart is not rare).

# Schema

`ImeProcessIdentity` establishes identity by facts a standard user cannot forge:

| Check | Source | Why it holds |
|---|---|---|
| `SessionId == 0` | `Process.SessionId` | Only services and their children run in session 0; a user session cannot start a process there. |
| image under `%ProgramFiles(x86)%\Microsoft Intune Management Extension\` (also `%ProgramW6432%`/`%ProgramFiles%` variants) | `QueryFullProcessImageName` (cross-bitness — IME is 32-bit, the agent may be 64-bit; `MainModule` throws there), `MainModule.FileName` fallback | Program Files is admin-writable only. Segment-aware prefix (`...Extension\`), `GetFullPath`-normalised, so a sibling folder or `..` does not pass. |
| file name exactly `IntuneManagementExtension.exe` | image path | `GetProcessesByName` matches the name without extension. |

Unknown facts (session `-1`, unresolved path) are **untrusted** — a false negative only delays
attach by one 5 s discovery tick, a false positive mutes the signal for the session.

Among trusted candidates the **oldest** by `StartTime` wins (the SCM-started service predates
anything transient). Untrusted name matches are logged once per PID as a Warning in the agent log
(`ignoring untrusted IntuneManagementExtension.exe candidate (PID, session, image)`) and never
attached; no timeline event is raised for them.

**Re-arm:** `OnImeExited` detaches, emits the event, and restarts the discovery timer. Only the
currently attached `Process` object is honoured (`ReferenceEquals`), so a late `Exited` from a
replaced or disposed handle cannot produce a duplicate event. A process that exits between probe
and subscription is handled by an explicit `HasExited` check after subscribing.

The watcher owns the `Process` handles its source returns (like `GetProcessesByName`) and disposes
them — tests must hand it fresh handles (`Process.GetProcessById`), not the ones they hold.

# Examples

* Fake started in session 1 from `Downloads\IntuneManagementExtension.exe` before the service is up →
  never attached; its exit emits nothing; the service arriving later is attached normally.
* Service exits (crash or restart) → `ime_process_exited` (PID, exit code, uptime), discovery
  re-armed, the restarted service is attached on the next tick and its later exit is reported too.
* Fake and service both running, fake listed first → the service is attached.

Covered by `ImeProcessWatcherIdentityTests` (pure identity table + stand-in `cmd.exe` processes
with injected identity facts).

# Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeProcessIdentity.cs`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeProcessWatcher.cs`
* `src/Agent/AutopilotMonitor.Agent.V2.Core.Tests/Monitoring/Ime/ImeProcessWatcherIdentityTests.cs`
* Related: [Local Admin Analyzer](local-admin-analyzer.md) (CWE-807 dynamic allowance).
