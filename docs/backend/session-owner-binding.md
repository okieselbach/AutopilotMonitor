---
type: Concept
title: Session Owner Binding — Session-Level Authorization for Agent Writes
description: Why tenant-level agent authentication (any device certificate, or a bootstrap token) was enough to rewrite every session in the tenant, how each Sessions row is now bound to the device identity that created it, the rule matrix that keeps every legitimate lifecycle working, the shadow-then-enforce rollout, and why enforcement needs the agent to rotate its session id rather than count an auth failure.
resource: src/Backend/AutopilotMonitor.Functions/Security/SessionOwnershipPolicy.cs
tags:
  - backend
  - security
  - authorization
  - sessions
  - agent
  - multi-tenant
timestamp: 2026-08-29T00:00:00+02:00
---

# Problem

Agent endpoints authenticate at **tenant** granularity: a chain-valid Intune MDM certificate whose
tenant stamp matches the request (see [client-cert-tenant-binding.md](client-cert-tenant-binding.md)),
or a bootstrap token minted for the tenant. They authorize writes at **session** granularity using a
session id the caller supplies — `IngestTelemetryFunction` takes it from the `PartitionKey` of the
first item, `RegisterSessionFunction` from the body, `ReportAgentErrorFunction` from the body. Until
2026-08-29 nothing on the Sessions row recorded which device created it, so nothing could be compared.

The consequence: one enrolled device — or anyone who saw a 6-character bootstrap short code — could,
for every other session of the tenant, forge `enrollment_complete` / failure events (flipping the live
status, firing webhooks and SLA logic), pollute the timeline, re-register the session with different
device metadata (which also redirects the Progress-Portal serial-knowledge proof), and — worst — send a
Signal-only batch that **fetches and clears the victim's pending ServerActions**, silently stealing
admin commands such as diagnostics collection. Session ids are GUIDs, but they travel in portal URLs
and tenant SignalR messages; they were never meant to be a capability.

# Mechanism

## Owner on the row

`SessionOwner` (Shared) is stamped by the backend from the validated caller identity — never from
agent input — and lives in six primary-only Sessions columns (`OwnerKind`, `OwnerThumbprint`,
`OwnerDeviceId`, `OwnerBootstrapCode`, `OwnerSerial`, `OwnerBoundAt`). They are listed in
`SessionIndexFieldManifest.PrimaryOnly`: never mirrored to `SessionsIndex`, never on `SessionSummary`,
so no certificate identity travels to clients.

| Auth path | Identity that binds | Proven by |
|---|---|---|
| Certificate | `OwnerThumbprint` + `OwnerDeviceId` (Intune device id from the certificate CN, lower-case GUID) | TLS handshake; the CN survives a certificate re-issue |
| Bootstrap token | `OwnerBootstrapCode` + `OwnerSerial` | Token possession + the serial the caller announced (header, unverified) |

`SecurityValidationResult.IntuneDeviceId` is new: the CN was already extracted for the Cloud PC
validator, now it is surfaced on the result for every certificate-authenticated request.

## Rule matrix

`SessionOwnershipPolicy.Evaluate(existingRow, validation, now)` is pure. It returns an outcome, the
owner to stamp (if any) and whether the caller's serial equals the row's serial.

| Caller | Row owner | Outcome | Stamps | Would reject |
|---|---|---|---|---|
| any | no row | `Fresh` | caller | no |
| any | no owner columns (legacy), serial equal | `ClaimLegacy` | caller | no |
| any | no owner columns, serial differs | `LegacySerialMismatch` | — | **yes** |
| any | — (validation carried neither thumbprint nor code) | `CallerUnidentified` | — | no |
| Cert | Cert, same thumbprint | `Match` | — | no |
| Cert | Cert, other thumbprint, same device id | `RebindCertRotation` | caller | no |
| Cert | Cert, other thumbprint and device id | `MismatchCert` (+ `serialMatch`) | — | **yes** |
| Cert | Bootstrap, same serial | `RebindBootstrapHandoff` | caller | no |
| Cert | Bootstrap, other serial | `MismatchBootstrapOwned` | — | **yes** |
| Bootstrap | Bootstrap, same code + serial | `Match` | — | no |
| Bootstrap | Bootstrap, otherwise | `MismatchBootstrap` | — | **yes** |
| Bootstrap | Cert | `DowngradeToBootstrap` | — | **yes** |

The tolerated rows are the legitimate lifecycle: agent restart and reboot (same certificate),
WhiteGlove Part 2 (same certificate), the install→runtime handoff where a bootstrap-registered session
continues under the MDM certificate once it exists, a re-issued certificate of the same Intune device,
and rows that predate the binding.

`MismatchCert` with `serialMatch=true` is the one shape that is *not* an attack: an Intune
re-enrollment **without a wipe**. `session.id` survives in `%ProgramData%\AutopilotMonitor`, but the
device gets a new Intune device id and therefore a new certificate identity. Under enforcement this
would be refused — which is why enforcement needs the agent-side rotation below.

## Where it runs

`SessionOwnerBindingObserver` is the single side-effect carrier; the three call sites hand it the
row they already hold:

* `RegisterSessionFunction.ProcessRegisterAsync` — the cascade-delete guard now returns the row
  (`EnsureWritableAndGetRowAsync`); the decision's owner goes into `StoreSessionAsync(registration,
  ownerToStamp)`, whose Replace preserves an existing owner when nothing new is stamped (including a
  Merge-stamped owner that landed after the initial read — the CAS re-read selects the owner columns).
  The bootstrap wrapper inherits this.
* `IngestTelemetryFunction.Run` — evaluated against `guardSessionRow` (zero extra reads) before
  `PersistItemsAsync`, so the Signal-only path that serves pending ServerActions is covered. Legacy
  claims and rebinds are written with `UpdateSessionOwnerAsync` (Merge, fail-soft).
* `ReportAgentErrorFunction` and its bootstrap wrapper — one point-read, observe only.

## Shadow telemetry (stage 1)

Same three carriers as the cert-tenant binding:

* **Denominator** — `requests | extend o = tostring(customDimensions.SessionOwnerBinding) | where
  isnotempty(o) | summarize count() by o` (request-row dimension via `RequestTelemetryMiddleware`;
  worker `LogInformation` never reaches App Insights).
* **Numerator** — `traces | where message startswith "AgentSessionOwnerBinding"` — one Warning per
  non-Match/non-Fresh request with `outcome`, `wouldReject`, `callerKind`, `ownerKind`, `serialMatch`,
  `endpoint`, `ver`.
* **Operator signal** — ops event `SessionOwnerMismatch` (Security / Warning, dual-registered in the
  admin alert-rule catalog) for would-reject outcomes, throttled to one per session+outcome per hour.
  `serialMatch=true` counts re-enrollments without wipe; `serialMatch=false` is a foreign device.

Nothing is refused in stage 1. `SessionOwnershipPolicy` deliberately has no `Rejects` member
(pinned by `SessionOwnershipPolicyTests.Stage1_has_no_Rejects_rule`).

## Enforcement contract (stage 2, not yet built)

* Backend: `Rejects(outcome)` = the would-reject set; register/ingest/error answer 403 with
  `RegisterSessionResponse.ErrorCode = Constants.AgentErrorCodes.SessionOwnerMismatch`
  (`"session_owner_mismatch"`). The code and field already exist so the agent could ship first.
* Agent (shipped dormant with this change): `BackendApiClient` reads `errorCode` off a JSON 401/403
  body into `BackendAuthException.ErrorCode`; `SessionRegistrationHelper` reacts to exactly that code
  by calling `rotateSession` once and re-registering immediately — **without** feeding
  `AuthFailureTracker` (five consecutive 403s would otherwise soft-shutdown a perfectly authorized
  agent). `BackendSessionRegistration.RotateSession` rotates `session.id` (keeping
  `whiteglove.complete`), re-targets `agentConfig.SessionId` and the `EmergencyReporter`, and drops
  `spool.jsonl` + `upload-cursor.json` because their lines carry the refused partition key. The
  `agent_started` event then reports `sessionRotated=true` + `previousSessionId`.
* Registration is the first session-scoped call of every agent start and everything after Phase 6
  reads `agentConfig.SessionId` live, so a mismatch on ingest can only mean an agent older than the
  rotation logic or a genuinely foreign caller.

Switch on only after: the shadow distribution has run for weeks, every `wouldReject=true` is
explained, and the fleet's `X-Agent-Version` is at or above the rotation-capable release. Then add
`Rejects`, delete the stage-1 pin test, update the trust pages (isolation model) and bump their
"Last reviewed" date.

# Residual risk (accepted)

* The serial is a caller-supplied header on both paths. A certificate holder who also knows a victim's
  serial can still claim a **legacy** row, or a **bootstrap-owned** row during its handoff window.
* One bootstrap token serves many devices; two devices on the same short code can still write into
  each other's session until the certificate handoff closes it.
* `SupersedeOrphanedPredecessorsAsync` is keyed on the announced serial and can still resolve foreign
  open sessions to `Incomplete`. Follow-up: restrict to rows without owner or with the same
  `OwnerSerial`, and require a minimum age.

None of these are closable without a signature scheme over the request body, which is deliberately
not built (it would duplicate the TLS proof).

# Citations

* `src/Backend/AutopilotMonitor.Functions/Security/SessionOwnershipPolicy.cs` — matrix, `WouldRejectUnderEnforcement`, row (de)serialization
* `src/Backend/AutopilotMonitor.Functions/Services/SessionOwnerBindingObserver.cs` — carriers, throttle, stamping
* `src/Shared/AutopilotMonitor.Shared/Models/Enrollment/SessionOwner.cs` — column names and kinds
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Sessions.cs` — `StoreSessionAsync(…, ownerToStamp)`, `UpdateSessionOwnerAsync`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Security/SessionRegistrationHelper.cs` — rotation branch
* `src/Agent/AutopilotMonitor.Agent.V2/Runtime/BackendSessionRegistration.cs` — `RotateSession`
* Tests: `SessionOwnershipPolicyTests`, `SessionOwnerBindingObserverTests`, `StoreSessionReregistrationPreserveTests`, agent `SessionRegistrationHelperTests`, `BackendSessionRegistrationTests`, `SessionIdPersistenceTests`, `BackendApiClientAuthClassificationTests`
* Grep marker: `SESSION-OWNER-BINDING`
