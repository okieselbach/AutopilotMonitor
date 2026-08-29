---
type: Concept
title: Local Admin Analyzer — account inventory, dormant accounts and Administrators membership
description: What the local_admin_analysis event asserts about a device at enrollment start and completion, why disabled accounts are inventoried (a /active:no backdoor re-enabled after enrollment is dormant at both scans), how Administrators-group membership is read from the SAM by well-known SID (NetLocalGroupGetMembers, independent of WMI and account state), why built-ins are matched by well-known RID (localized Guest/Administrator), why the shutdown-time dynamic allowance never covers a local SAM account (CWE-807 — the login state is attacker-controlled), which folders are not profiles, which members are scored versus only listed, and why the built-in Administrator's enabled state is reported but not scored.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Analyzers/LocalAdminAnalyzer.cs
tags:
  - agent
  - security
  - local-admin
  - analyze-rules
  - bypass-detection
timestamp: 2026-08-30T00:00:00+02:00
---

# Local Admin Analyzer

`LocalAdminAnalyzer` emits one `local_admin_analysis` event at agent startup and one at
shutdown. The pair is a before/after inventory of local accounts on the device; the backend
rule `ANALYZE-ID-002` alerts when the finding is `unexpected_local_admins_detected`.
The threat model is the classic Autopilot bypass: a person with a Shift+F10 SYSTEM console
during OOBE/ESP who creates a persistent local administrator.

# Schema

## Checks

| Check | Source | What it yields |
|---|---|---|
| `bypass_nro` | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE\BypassNRO` | value + `flagged` |
| Local accounts | WMI `Win32_UserAccount WHERE LocalAccount = True` (Name, Disabled, SID) | `accounts_checked`, `account_details` (name / disabled / administrators_member), `unexpected_accounts`, `builtin_administrator_enabled` |
| Administrators membership | `NetLocalGroupGetMembers` level 2 on the group resolved from `S-1-5-32-544` (`LocalGroupNativeMethods`) | `administrators_group.{enumerated,error_code,members}`, `unexpected_admin_members` |
| Profile folders | `C:\Users` top-level directories, junctions and `$<32 hex>` folders skipped | `profiles_found`, `unexpected_profiles` |

## Allowed list

Built-in names (`Administrator`, `Guest`, `DefaultAccount`, `WDAGUtilityAccount`,
`defaultuser0/1/2`, `kioskUser0` — the Assigned Access autologon account of kiosk profiles —
and the non-account folders `Public`, `Default`, `Default User`, `All Users`) plus the
tenant's *Allowed Local Accounts* (glob `*`/`?`, case-insensitive).
`defaultuser1/2` stay allowlisted deliberately: they do appear during OOBE enrollment.

**Well-known RIDs.** Built-in accounts are additionally recognised by RID on the machine SID
(`S-1-5-21-…-500` Administrator, `-501` Guest, `-503` DefaultAccount, `-504`
WDAGUtilityAccount), for the WMI inventory and for the SAM-member fallback alike. Localized
installs name them `Gast`, `Invité`, `Гость`, `Administrateur`, … — disabled by default and
therefore invisible before disabled accounts were inventoried; without the RID match every
non-English device would flag its own Guest. Name matching is unchanged (an ordinary-RID
account named `Gast` is still unexpected).

## Dynamic allowance at shutdown

The shutdown run adds the logged-in users (explorer.exe owners outside session 0) to the
allowed list — but **only names that are not a local SAM account**. The WMI inventory is read
first; a logged-in name found in it is rejected for both the account and the profile check
and reported as `logged_in_local_accounts` (Warning in the agent log).

Why this is safe for false positives: in real sessions the logged-in identity is an Entra or
domain user (user-driven, hybrid, or an Entra user signing in to a self-deploying device
after ESP). Those never appear in `Win32_UserAccount`; the allowance only ever mattered for
their `C:\Users` folder — and `unexpected_profiles` alone scores 0. A logged-in *local*
account is the one case the allowance changed the score, and that is the attack: an account
created after the startup scan that signs itself in before the shutdown scan (CWE-807 —
exempting on a condition the adversary controls). Kiosk autologon (`kioskUser0`) is covered
by the built-in list instead.

## Profile folders that are not profiles

Junctions (`All Users`, `Default User` and localized variants such as `Все пользователи`,
all pointing at ProgramData / Default) and `$<32 hex>` folders are skipped entirely — neither
can be the profile of a local account (a SAM account name is at most 20 characters), so they
can never contribute to the profile-overlap score; listing them was payload noise.

## Evaluation (`EvaluateAccounts`, pure)

* Every local account is evaluated, **disabled ones included**; the disabled state is reported
  per account instead of excluding the account.
* An account is an Administrators member when its SID matches a group member, or — SID
  unavailable — its name matches a member of the local machine domain.
* `unexpected_accounts` = accounts not matching the allowed list (any state).
* `unexpected_admin_members` = unexpected accounts holding Administrators membership, plus
  local-domain members not on the allowed list that WMI did not return at all (WMI failure
  or provider gap — the SAM is authoritative for membership).
* Members outside the machine domain (Entra role SIDs such as `S-1-12-1-…` on Entra-joined
  devices, domain groups on hybrid devices, unresolved/deleted SIDs) are **listed for delta
  comparison but never scored** — they are expected on joined devices.
* `builtin_administrator_enabled` = `!Disabled` of the RID-500 account (null when absent).
  Reported, not scored: whether OOBE enables it transiently is not established; the field
  collects evidence first.

## Scoring

| Signal | Weight |
|---|---|
| `BypassNRO = 1` | +20 |
| any `unexpected_accounts` | +40 |
| any `unexpected_admin_members` | +40 |
| unexpected account with a matching `C:\Users` folder | +40 |

Capped at 100. `0` → Info `no_unexpected_admins_detected`; `<40` → Info
`bypass_nro_flag_only`; `40–79` → Warning, `≥80` → Error, both
`unexpected_local_admins_detected`. A dormant backdoor with admin membership therefore lands
at 80 (Error) without ever having a profile folder.

## Failure behaviour

WMI and the SAM read are independent; either failing is logged at Warning with the native
status (`NET_API_STATUS=…`) and the other source still evaluates. The event carries
`administrators_group.enumerated=false` + `error_code` so a "no members" result is never
mistaken for an empty group.

# Examples

`net user backdoor P@ss /add /active:no` + `net localgroup Administrators backdoor /add`
during OOBE, re-enabled by a scheduled task after enrollment: at both scans the account is
disabled; before this change it was skipped entirely. Now `unexpected_accounts=[backdoor]`,
`unexpected_admin_members=[backdoor]`, `account_details[backdoor].disabled=true` →
confidence 80, Error.

`net user backdoor P@ss /add` + `net localgroup Administrators backdoor /add` during ESP,
then an interactive sign-in as `backdoor` (fast user switching) before enrollment completes:
the shutdown scan finds `backdoor` in the SAM inventory → not allowed dynamically →
`unexpected_accounts=[backdoor]`, `unexpected_admin_members=[backdoor]`, profile overlap →
confidence 100, Error, `logged_in_local_accounts=[backdoor]`. Before this change the login
alone silenced the finding to 0.

German device, tenant allowed list empty: `Gast` (RID 501, disabled) and `Administrator`
are inventoried and matched by RID → `unexpected_accounts=[]`, confidence 0.

`net localgroup Administrators adm-helpdesk /add` where `adm-*` is allowlisted: not flagged
(the allowed list is the tenant's statement of intent), but `administrators_group.members`
shows the new membership in the shutdown event for delta review.

# Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Analyzers/LocalAdminAnalyzer.cs`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/LocalGroupNativeMethods.cs`
* `src/Agent/AutopilotMonitor.Agent.V2.Core.Tests/Monitoring/Analyzers/LocalAdminAnalyzerEvaluateAccountsTests.cs`
* `src/Agent/AutopilotMonitor.Agent.V2.Core.Tests/Monitoring/Analyzers/LocalAdminAnalyzerDynamicAllowanceTests.cs`
* `rules/analyze/ANALYZE-ID-002.json`
