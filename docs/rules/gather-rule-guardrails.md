---
type: Concept
title: Gather Rule Guardrails
description: >-
  How gather-rule collectors are fenced in — the allowlists in rules/guardrails.json,
  the hard blocks that survive unrestricted mode, and why every collector must call a
  guard rather than trusting rule.Target.
resource: src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather
tags: [gather-rules, security, guardrails, agent]
timestamp: 2026-07-22
---

# Concept

A gather rule is authored by a **tenant administrator** through `POST /api/rules/gather` and executed by the agent as **SYSTEM** on an enrolling device. The backend does not validate `collectorType`, `target`, or `parameters` at all — it only enforces scope/emit-mode fields, a 1 MB body cap, and that the rule lands in the caller's own tenant partition. Everything that decides *what a rule may reach* is therefore enforced **on the agent**, in `GatherRuleGuards`.

This is the security boundary. The portal's `ValidationIndicator` is a convenience: it does not block submit, and the API is reachable directly.

# Schema

Allowlists come from `rules/guardrails.json`, embedded into the agent assembly as
`AutopilotMonitor.Agent.V2.Core.Resources.guardrails.json` (see the `EmbeddedResource`
item in `AutopilotMonitor.Agent.V2.Core.csproj`) and regenerated into the portal's
`utils/guardrails.generated.ts` by `node rules/scripts/combine.js`.

| Guard | Source list | Match rule |
| --- | --- | --- |
| `IsRegistryPathAllowed` | `registryPrefixes` | prefix, boundary `\` or end |
| `IsFilePathAllowed` | `filePrefixes` | `Path.GetFullPath` first, then prefix + boundary |
| `IsWmiQueryAllowed` | `wmiQueryPrefixes` | prefix, boundary whitespace or end |
| `IsCommandAllowed` | `allowedCommands` | **exact** match, trimmed — never prefix |
| `IsEventLogChannelAllowed` | `eventLogChannels` | prefix, boundary `/` or end |

**Hard blocks live in code, not in the JSON**, so a parse error cannot lift them — on a
parse failure the allowlists fall back to empty and everything is blocked (fail-closed),
but the hard blocks must hold regardless of configuration:

* `BlockedUsersPrefix` (`C:\Users`) and `AdditionalHardBlockedPathPrefixes`
  (`C:\Windows\System32\config`) — exception only for `%LOGGED_ON_USER_PROFILE%`
  under `AppData\Local` / `AppData\Roaming`.
* `HardBlockedCommandPatterns` — download, user/group creation, boot config,
  persistence, destructive operations; plus `MaxCommandLength`.
* `HardBlockedEventLogChannels` — `Security` (audit trail of user behaviour) and the
  PowerShell channels (script-block logging routinely contains secrets in clear text).

`rules/guardrails.json` mirrors the blocked lists (`blockedFilePrefixes`,
`blockedEventLogChannels`) **for display only**. Enforcement is the C# constant.

# Examples

Every collector must call its guard and return `context.EmitSecurityWarning(...)` on
refusal — that emits the `security_warning` event that makes a block visible in the
timeline instead of failing silently.

```csharp
// FileCollector / JsonCollector / XmlCollector
if (!GatherRuleGuards.IsFilePathAllowed(filePath, context.UnrestrictedMode, userProfilePath))
    return context.EmitSecurityWarning(rule, "file", filePath);
```

Two collectors were added to this contract on 2026-07-22 after shipping without it:

* **`LogParserCollector`** read any path in `rule.Target`, bypassing even the `C:\Users`
  hard block. It cannot guard `rule.Target` directly — a wildcard filename makes
  `Path.GetFullPath` throw — so it guards **the directory** when the filename contains
  `*`/`?`, and re-guards **each resolved path** after wildcard expansion (a junction can
  surface a file the directory check did not cover).
* **`EventLogCollector`** read any channel, including `Security`.

`ImeLogPathOverride` comes only from the local `--ime-log-path` CLI flag, never from
remote config. The operator using it is already a local admin, so it relaxes the
allowlist exactly the way unrestricted mode does — hard blocks still apply.

# Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleGuards.cs`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleContext.cs` — `EmitSecurityWarning`
* `src/Agent/AutopilotMonitor.Agent.V2.Core.Tests/Monitoring/Gather/GatherRuleCollectorGuardTests.cs`
* `src/Backend/AutopilotMonitor.Functions/Functions/Rules/GatherRulesFunction.cs` — what the backend does *not* validate
* `rules/guardrails.json`, `rules/scripts/combine.js`
* Customer-facing counterpart: `autopilotmonitor-docs/rules/gather-rules.md` (§ Security guardrails)
