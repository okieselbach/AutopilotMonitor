---
type: Concept
title: Admin Identity Binding — tid + oid behind every cross-tenant-role UPN
description: Why a GlobalAdmins or DelegatedAdmins row keyed on a UPN string must never confer access by itself in an API that accepts tokens from every Entra tenant, the AdminIdentityBindings table that pins each such UPN to its immutable home tenant id and object id, the fail-closed resolution order in both role services, the first-sign-in object-id pin, and the explicit rebind surface.
resource: src/Backend/AutopilotMonitor.Functions/Services/AdminIdentityBindingService.cs
tags:
  - backend
  - security
  - authorization
  - multi-tenant
  - global-admin
  - delegated-admin
timestamp: 2026-08-29T00:00:00+02:00
---

# Problem

The API is a multi-tenant Entra application on purpose: `AuthenticationMiddleware` accepts a token from
any tenant (issuer prefix `login.microsoftonline.com/` or `sts.windows.net/`), and signature, audience,
lifetime and algorithm are validated strictly. Tenant-member roles (`TenantAdmins`) are keyed on
`(tid, upn)` and therefore bound to the verified tenant.

The two cross-tenant tiers were not. `GlobalAdmins` (GlobalAdmin / GlobalReader) and the delegated MSP
tier (`DelegatedAdmins` + `TenantGroupAssignments`) resolved their role from the **UPN string alone**
(`upn`, falling back to `preferred_username`). Both claims are mutable and reusable across tenants:

* **Domain re-registration**: if the domain behind a platform admin's UPN lapses or moves, whoever
  verifies it in a fresh tenant can mint the identical UPN; the token is genuine, only `tid` differs.
* **UPN recycling**: a User Administrator in the home tenant can re-assign the UPN to another account;
  same `tid`, different `oid`.
* **MSP variant**: the delegated tier was gated only on the caller's home tenant being Pro — which any
  tenant can self-provision via the trial endpoint.

Any of these yielded full platform scope: raw table dumps including `TenantConfiguration` secrets,
tenant offboarding, GA management. Microsoft's own guidance (post-nOAuth) is explicit: authorization
data must be keyed on the immutable `tid` + `oid` pair, never on `upn` / `preferred_username`.

# Mechanism

## One binding per UPN

`AdminIdentityBindings` (PK `Bindings`, RK = lowercase UPN) holds, for every UPN that carries a
cross-tenant role, the identity that may use it:

| Column | Meaning |
| --- | --- |
| `TenantId` | The admin's **home** Entra tenant — must equal the JWT `tid`. Mandatory at grant time. |
| `ObjectId` | The admin's Entra object id — must equal the JWT `oid`. Empty until pinned. |
| `BoundBy`, `BoundDate` | Operator provenance. |
| `ObjectIdPinnedDate` | When the object id was fixed (grant time, or the first matching sign-in). |

A single table rather than columns on three role tables: a UPN string belongs to exactly one identity
at a time, a person may hold a GlobalAdmins row *and* delegated assignments, and the role rows stay
what they are — grants. The binding says whose grants they are.

## Resolution order (both role services)

`AdminIdentity` is the record `(Upn, TenantId, ObjectId)` built from a validated principal; it is
`null` when any of the three claims is missing (app-only tokens, foreign IdP shapes). Both
`GlobalAdminService.GetGlobalRoleAsync(AdminIdentity?)` and
`DelegatedAdminService.GetScopeAsync(AdminIdentity?)` follow the same order:

1. `null` identity → no role, no storage read.
2. Resolve the **row** by UPN (cached 30 s, unchanged from before).
3. No row / empty scope → return; the binding is not even read. Ordinary tenant users cost one cached
   read and never produce a binding log line.
4. `AdminIdentityBindingService.IsBoundAsync(identity)` → false ⇒ no role / empty scope.
5. Delegated only: the Pro home-tenant entitlement gate, **after** the binding check — an unbound caller
   never triggers an edition lookup.

`IsBoundAsync` is the single choke-point: no binding ⇒ false; `TenantId` mismatch ⇒ false (checked
before anything else, so a foreign-tenant token can never claim the pin below); pinned `ObjectId`
mismatch ⇒ false; unpinned binding + matching tenant ⇒ **pin** the caller's `oid` and verify against
what was actually stored afterwards. Every false outcome is logged at Warning with its reason
(`[IdentityBinding] TENANT MISMATCH …` / `OBJECT-ID MISMATCH …` / `holds no identity binding`), the
one level the worker forwards to Application Insights; matches are silent. Pins are logged at Warning
once — they are one-off, operator-relevant events.

The pin is a conditional update in the repository (`TryPinIdentityObjectIdAsync`: only onto an unpinned
row homed in the caller's tenant, ETag-guarded; a 412 re-reads and the caller compares against the
winner). Two accounts racing the first sign-in inside the home tenant therefore leave exactly one bound.

Row lookups stay cached by UPN; the binding has its own 30 s cache keyed by UPN and is invalidated on
pin, rebind and removal. A rebind on one scaled-out instance converges on the others within seconds.

## Grants bind first

`AddGlobalAdminAsync`, `DelegatedAdminService.UpsertAsync` and `AssignGroupAsync` take
`homeTenantId` (GUID, required) and `objectId` (GUID, optional) and call `EnsureBoundAsync` **before**
writing the role row. `EnsureBoundAsync` creates the binding, keeps a compatible one (same tenant; the
supplied object id equals the pin or upgrades an unpinned one), and throws
`IdentityBindingConflictException` for a different tenant or a different pinned object id — the
functions map that to **409**. A grant can therefore never silently re-home a UPN.

Why the object id may be omitted: the operator granting an external MSP admin has no directory access
to the partner tenant, so the person's `oid` is frequently unknown at grant time; the home tenant is
always known (it is who the grant is for). The first sign-in from that tenant pins it; from then on a
recycled UPN in the same tenant is refused.

## Explicit rebind surface

| Route | Policy | Purpose |
| --- | --- | --- |
| `GET global/identity-bindings` | GlobalReadOrAdmin | Audit which tenant / object id each grant is usable from. |
| `PUT global/identity-bindings/{upn}` | GlobalAdminOnly | Replace the binding: move a UPN to another tenant, or re-pin after a legitimate account re-creation (omit `objectId` to clear the pin). |
| `DELETE global/identity-bindings/{upn}` | GlobalAdminOnly | Make every role row of the UPN inert without touching the rows. Self-removal is refused. |

Both mutations are audited under the binding's home tenant (`AdminIdentityBinding` entity, previous
values in the details) and logged at Warning. The list endpoints of delegated admins and tenant groups
return `bindings` alongside their rows so the management UI can show a per-UPN context pill (home tenant,
pin state) and require the home tenant in its grant / assign forms.

## Lifecycle and secondary effects

* `AdminIdentityBindings` is in `TableNames.All`, in `CriticalBackupTables.All` and in
  `AuthTablesFullRestoreForbidden` (single-row restore only, like the other auth tables).
* Tenant offboarding wipes bindings whose `TenantId` is the offboarded tenant (property-only bucket):
  once a home tenant is gone, its admins' grants become inert.
* `RequestContext.ObjectId` carries the JWT `oid`; `AdminIdentity.FromRequestContext` serves the
  handlers that resolve roles outside the middleware (SignalR group join/leave, health checks).
* The throttle identity (`GetCallerId`) now prefers `oid` over the UPN, so two accounts in different
  tenants that share a UPN string no longer share a rate-limit bucket. Audit and presence rows keep the
  UPN as their display key; they already carry the tenant id.
* Legacy role rows without a binding are **inert, not grandfathered**. Rolling this out therefore means
  seeding the binding rows for the existing admin UPNs (their `tid` and `oid` are recorded per login in
  `UserActivity`) *before* the backend deploy — an unseeded platform admin locks themselves out of every
  GlobalAdminOnly route, including the rebind surface.

# Examples

A GlobalAdmin row for `admin@vendor.example` bound to tenant `1111…` with object id `aaaa…`:

| Token | Result |
| --- | --- |
| `tid=1111… oid=aaaa… upn=admin@vendor.example` | GlobalAdmin |
| `tid=2222… oid=aaaa… upn=admin@vendor.example` | no role — `TENANT MISMATCH` warning |
| `tid=1111… oid=bbbb… upn=admin@vendor.example` | no role — `OBJECT-ID MISMATCH` warning |
| `tid=1111… upn=admin@vendor.example` (no `oid`) | no role — identity incomplete |

Same row, binding without an object id: the first `tid=1111…` sign-in pins its `oid` and is admitted; a
`tid=2222…` sign-in before that is refused **and does not pin**.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Security/AdminIdentity.cs` — the identity record and its factories.
* `src/Backend/AutopilotMonitor.Functions/Services/AdminIdentityBindingService.cs` — verdict, pin, grant-time binding, rebind; the table entity.
* `src/Backend/AutopilotMonitor.Functions/Services/GlobalAdminService.cs`, `DelegatedAdminService.cs` — resolution order.
* `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableAdminRepository.cs` — `TryPinIdentityObjectIdAsync` and the binding CRUD.
* `src/Backend/AutopilotMonitor.Functions/Functions/Admin/IdentityBindingManagementFunction.cs` — the rebind surface and the shared `IdentityBindingRequest.Validate`.
* `src/Backend/AutopilotMonitor.Functions.Tests/AdminIdentityBindingServiceTests.cs`, `AdminIdentityBindingAuthorizationTests.cs` — verdict matrix, race semantics, and the middleware-level denials for foreign-tenant and recycled-UPN tokens.
* [Client-Certificate Tenant Binding](client-cert-tenant-binding.md) — the same "chain-valid is not tenant-bound" argument on the device side.
