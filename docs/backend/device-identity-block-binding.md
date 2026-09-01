---
type: Concept
title: Device Identity Block Binding — Kill Switch Beyond the Serial Header
description: Why a device block keyed only by the caller-declared X-Device-SerialNumber could be dodged by omitting or forging the header (CWE-807), how a block is now mirrored onto the device's certificate identity as alias rows resolved at block time, how session-scoped watchdog blocks are finally decided once the session is known, why the progress-portal lookup dropped its newest-100 horizon, and the time budget that keeps the Graph device validators inside the agent's client timeout.
resource: src/Backend/AutopilotMonitor.Functions/Services/KillSwitchEvaluator.cs
tags:
  - backend
  - security
  - kill-switch
  - blocked-devices
  - agent
  - progress-portal
  - graph
timestamp: 2026-09-01T00:00:00+02:00
---

# Device Identity Block Binding

## Threat

`BlockedDevices` is keyed `(tenantId, serial)` and the kill switch read the serial verbatim from
`X-Device-SerialNumber`. A blocked device therefore had two exits: omit the header (the evaluator
skipped the device leg entirely) or send a foreign serial. In both cases the admin block, the
excessive-events watchdog and the **kill signal** never reached the agent. Mythos finding CWE-807
("reliance on untrusted inputs in a security decision").

What the request does carry that the caller cannot choose: the client certificate. Its Subject CN
is the Intune device id (`SecurityValidator.TryGetIntuneDeviceIdFromCertSubject`), surfaced as
`SecurityValidationResult.IntuneDeviceId` at both kill-switch call sites. Live coverage (request
dimension `CertTenantBinding=Match`) is effectively every register/ingest/config request.

## Design: alias rows, resolved at block time

A rejected alternative was a standing device-index table (identity → serial claim written on every
request). It would have added a per-request write path, a cache and a retention sweep for an
event that happens ~30 times per 90 days, and the accompanying performance argument (replacing
the supersede partition scan) did not hold: RegisterSession runs at p50 ≈ 100 ms including that
scan.

Instead the block itself is mirrored:

| RowKey (PK = tenantId) | Role | Extra columns |
|---|---|---|
| `EscapeDataString(UPPER(serial))` | primary row (unchanged) | `AliasDeviceIds` — comma-separated lower-case GUIDs |
| `id:{intuneDeviceId}` | alias — same block fields (BlockedAt/UnblockAt/Action/Reason/BlockedSessionIds/BlockedByEmail/DurationHours) | `SerialNumber` = primary's canonical serial, `IsAlias = true` |

* **Resolution.** `BlockedDeviceService.BlockDeviceAsync` asks `ISessionRepository.GetOwnerDeviceIdsForSerialAsync`
  for the `OwnerDeviceId`s the serial has registered sessions under (Sessions partition query,
  newest first, cap 5, fail-soft). Callers (admin `POST /api/devices/block`, watchdog) stay
  serial-keyed — no API or UX change.
* **Enforcement.** `KillSwitchEvaluator.EvaluateAsync` runs serial leg → identity leg → version
  leg. The identity leg only runs when the serial leg missed and a certificate identity exists;
  the honest case pays no second lookup. An identity hit logs both serials (header vs. row) and
  emits the existing throttled `KillSignalDelivered` ops event keyed on the row's serial.
* **Cache.** Same `BlockedDeviceService` cache, second key namespace `tenant|id:<guid>` — same
  30-s revalidation, same negative entries. The lazy tenant load seeds serial keys only (the
  listing hides alias rows and its DTO is the admin/MCP wire shape — `AliasDeviceIds` never
  leaves storage); an identity key is point-read on its first miss, exactly like a serial.
* **Lifecycle.** Unblock deletes aliases before the primary and returns their ids so caches drop
  them; expiry cleanup sweeps aliases like any row; tenant offboarding wipes the partition. The
  listings (admin UI, MCP `list_blocked_devices`) skip `IsAlias` rows — no duplicates, no wire
  change; `MigrateLegacyRowKeyAsync` skips them too (it would otherwise re-key an alias onto the
  serial key and delete it).
* **Keys cannot collide:** serial keys pass through `EscapeDataString` (`:` → `%3A`); alias keys
  start literally with `id:`.

Request dimension `DeviceIdentityBinding` (`NoIdentity | Match | IdentityBlocked`) on the
unsampled request row is the denominator: `NoIdentity` is the coverage gap (bootstrap-token
callers, non-GUID CNs), `IdentityBlocked` the finding actually firing.

## Block scopes and channels

`BlockedSessionIds` marks a session-scoped block (the watchdog's auto-block of a runaway
session): only those sessions are blocked and a new enrollment on the same device lifts it.
That branch was unreachable — the evaluator never passed a session id — so every auto-block
acted as a blanket 24-h device block.

* **Telemetry channel.** The pre-body evaluation answers immediately for whole-device blocks and
  kills. A session-scoped verdict is carried until the body's session id is known, then
  re-evaluated with it: blocked session → blocked response; different session → auto-unblock
  (primary + aliases) and the batch proceeds. The version leg runs again on purpose (the device
  leg had short-circuited it). Additionally, when the Sessions row's registered serial differs
  from the header, the row's serial is held against the block list too.
* **Config channel.** Has no session and stays a blanket report: the agent only logs
  `DeviceBlocked` from config and acts on `DeviceKillSignal`, so the new session it registers
  lifts a session-scoped block on the telemetry channel.

## Progress-portal lookup

`GET /api/progress/sessions/lookup` used to scan the newest 100 SessionsIndex rows client-side, so
a device whose enrollment was older than that horizon answered "Device Not Found" — indistinguishable
from a typo, and the member substring search could land on a newer device containing the fragment.
`ProgressPortalFunction.ResolveSessionAsync` now resolves exact serial, then exact device name,
through server-side `eq` filters on the tenant partition (`FindNewestSessionIdBySerialAsync` /
`…ByDeviceNameAsync`, stored form or upper-case, newest by inverted-tick RowKey), then point-reads
the session. Only members/GA fall through to the previous substring page; roleless callers stay
exact-or-nothing (the knowledge proof). Index rows written before the column was projected are not
matched by `eq` — immaterial for a current enrollment.

## Graph validator budget

The four Graph-backed device validators (Autopilot, corporate identifier, device association,
Cloud PC) used the unnamed `HttpClient` — 100-s default timeout — two attempts each, sequentially,
with an uncancellable token retry chain underneath. Observed: GetAgentConfig tails of 30–102 s
while the agent's own client gives up after 30 s (`BackendClientFactory`), so the backend finished
work for a request nobody was waiting on. `DeviceValidationBudget` gives the chain one 20-s token
(created in `SecurityValidator` around the validator block) and each attempt a linked 8-s token
that also bounds token acquisition; budget exhaustion is a transient result (never cached) → the
existing 503 + Retry-After 30, which the agent's 10/30/60-s retry handles. A second attempt is
skipped once the chain token is spent. Transient failures are rare (≈ 1–2 per day platform-wide),
so no stale-while-error reuse of an expired positive result was built — that would have been a
fail-open trade without a need.

## Residuals

* A device never seen with a certificate identity (only bootstrap-token sessions, or a CN that is
  not a GUID) has no alias; the serial leg is all that applies — measurable as `NoIdentity`.
* An attacker who forges the serial from the very first request is a fresh, unknown device to the
  platform; only a future "block by identity" admin action could target it.
* A re-enrolled device receives a new Intune device id; its earlier alias goes stale (harmless — a
  cooperative agent announces its real serial, the primary row still matches).

# Citations

* `src/Backend/AutopilotMonitor.Functions/Services/KillSwitchEvaluator.cs` — three-leg evaluation, verdict with `BlockedSessionIds` / `IdentityBinding`
* `src/Backend/AutopilotMonitor.Functions/Services/BlockedDeviceService.cs` — cache namespaces, alias resolution, auto-unblock through the primary serial
* `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableDeviceSecurityRepository.cs` — `IdentityRowKey`, alias write/delete, listing and migration skips
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.DeviceLookup.cs` — owner-id and exact serial/device-name lookups
* `src/Backend/AutopilotMonitor.Functions/Functions/Ingest/IngestTelemetryFunction.cs` — pre-body / post-body evaluation, row-serial check
* `src/Backend/AutopilotMonitor.Functions/Functions/Progress/ProgressPortalFunction.cs` — `ResolveSessionAsync`
* `src/Backend/AutopilotMonitor.Functions/Security/DeviceValidationBudget.cs`, `SecurityValidator.cs` — chain and attempt budgets
* `src/Backend/AutopilotMonitor.Functions/Security/DeviceIdentityBinding.cs`, `Middleware/RequestTelemetryMiddleware.cs` — request dimension
* Tests: `KillSwitchEvaluatorTests`, `BlockedDeviceServiceTests`, `TableDeviceSecurityRepositoryKeyTests`, `ProgressPortalFunctionTests`, `AutopilotDeviceValidatorTests`
* Related: [session-owner-binding.md](session-owner-binding.md), [client-cert-tenant-binding.md](client-cert-tenant-binding.md)
