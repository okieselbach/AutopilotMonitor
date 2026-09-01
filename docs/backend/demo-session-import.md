---
type: Concept
title: Demo Session Import — Cloning a Session into the Operator's Own Tenant
description: How representative enrollment sessions (own, or from a customer tenant after a review gate) are imported under a new session id into the operator tenant gktatooine.net for live demos — the exact inverse of the 23-step deletion cascade, with identity scrubbing, a zero-leak assertion, insert-only writes under the new id, automatic rollback, and provenance in the globaladmin annotation lane. Found again through the annotation note search.
resource: .claude/commands/scripts/import_session.py
tags:
  - backend
  - operator-tooling
  - demo
  - table-storage
  - annotations
timestamp: 2026-09-01T23:30:00+02:00
---

# Problem

Live demos need sessions worth showing — failures, Wi-Fi→LAN switches, WhiteGlove, Hybrid, Cloud PC, geographic spread. The operator's own tenant (gktatooine.net) produces two VMs in one city. A separate demo tenant was considered and rejected: `DomainName` cannot be set through any API, the portal would need a tenant pin, and a second dataset would need its own upkeep. Instead, interesting sessions are **imported into the own tenant** under a new session id, scrubbed of customer identity, and simply live on next to the live sessions. The demo mode (`web/demo-presentation-mode.md`) already presents that tenant as a customer's.

Imported sessions keep their original timestamps, so they fall out of every dashboard/geo/SLA window eventually. They are found again through **annotations**: describe the session once in a note ("wifi switch at minute 3"), then search for that — the annotation lists accept `?q=` (`session-annotations.md`).

# Model

The import is the inverse of the per-session deletion cascade. `DeletionManifestBuilder.BuildAsync` (Functions/Services/Deletion) is the test-enforced list of every row a session owns — 23 steps, from Events to the Sessions/SessionsIndex tombstone — and the import exports exactly that set, rewrites it, and inserts it under a fresh id.

**Key rewriting.** Every key and every string column goes through one replacement table (longest token first, case-insensitive): source tenant → target tenant, source session id → new id, plus the scrub tokens. That single rule covers every key shape at once — `Events` PK `{t}_{sid}`, `SessionsIndex` RK `{invertedTicks}_{sid}`, `EventTypeIndex` PK `{t}_{eventType}` / RK `{invertedTicks}_{sid}`, the five V2 index tables (`IndexRowKeys.cs`), `AppInstallSummaries` RK `Sanitize({sid}_{app})`, the annotation RK `{sid}_{lane}`, `SessionTenantLookup` PK `sid`. Because timestamps are not shifted, the time components of those keys stay as they are. `Sessions.IndexRowKey` is set to the rewritten `SessionsIndex` RowKey so the mirror pair stays consistent.

**What does not travel.** `Owner*` (session-owner binding — device certificate identity, `SessionIndexFieldManifest.PrimaryOnly`), `PendingActions*`, `DeletionState`, `PendingDeletionManifestId`, and `DiagnosticsBlobName/-Destination` (a diagnostics ZIP is never copied: IME logs cannot be scrubbed reliably and a CustomerSas pointer would name the customer's storage account). Source annotations are not copied either.

**What is transformed beyond keys.** `Events.EventId` becomes `uuid5(newSid, oldEventId)` (the web's dedupe key). Chunked `PayloadJson` columns on `Signals`/`DecisionTransitions` (`TableStorageChunking`, 30 000 chars) are reassembled, rewritten and re-chunked. `DeviceSnapshot.Props_{eventType}` — an independent copy of the device event payloads — is rewritten like any other string. Rows are read and written with `odata=minimalmetadata` so the `@odata.type` annotations (Edm.DateTime, Edm.Int64, Edm.Guid) survive; without them a DateTime column silently turns into a string on re-insert.

**Contributions, not copies.** `SoftwareInventory` is tenant-wide with a `SessionCount` per (vendor, name, version). The import decodes the session's `SessionInventoryContributions` side-row (`SoftwareKeysJsonCodec`, raw or gzip+base64) and bumps each counter with an ETag merge (bounded retry), creating the row from the source tenant's metadata when the target does not have it — the same semantics as `IncrementSoftwareInventoryEntryAsync`. `DeviceHistories` (keyed by serial) is deliberately not touched.

**Scrubbing** (default on for a foreign source, off for the own tenant): every UPN found in any string → a name from a fixed Star-Wars pool `@gktatooine.net` (stable per source UPN; matches the tenant's existing `luke.skywalker`); `X.onmicrosoft.com` → `gktatooine.onmicrosoft.com`, other source domains → `gktatooine.net`; `DeviceName` → `DESKTOP-XXXXXXX`; the serial format-preserving random; 40-hex thumbprints, MACs and public IPv4 addresses random (TEST-NET-3 for IPs). Vendor domains (microsoft.com, windows.com, …) are left alone. Geo can be overridden per import (`--geo`), rewriting the `Geo*` columns and the `device_location` event.

**Provenance** is a row in the session's `globaladmin` annotation lane — `import: source=<tenant>/<sid> at=<ISO> scrub=on|off` — invisible to tenants and in demo mode; the `operator` and `tenantadmin` lanes stay free for the demo story. `list` enumerates imports from that lane.

# Safety model

- **Insert-only under the new id.** Every write is an Insert Entity; a collision fails with 409 and never overwrites. The single exception is the `SoftwareInventory` counter merge, ETag-guarded like the ingest path.
- **Key invariant.** Before the first write, every PartitionKey or RowKey to be written must contain the new session id; the rollback's delete refuses any key that does not. A rollback can therefore never reach a row that belonged to anyone else.
- **Zero-leak assertion.** Before the first write, no source tenant id, session id, domain, UPN, serial, device name, thumbprint, MAC or public IP may remain in any string or key; `verify` repeats the scan on the rows read back.
- **Atomic in effect.** Any failed write triggers a rollback of everything written so far (inserts deleted, counters decremented, self-created inventory rows deleted while `FirstSessionId` still points at the import). A crash mid-way is recoverable with `rollback` from the flushed manifest.
- **`apply --dry-run`** runs every check and prints the write plan and the session row as it would be written, writing nothing. `plan` (export + mapping + scrub report) is the review gate; a foreign source additionally needs an explicit confirmation.
- **Removal goes through the product.** Admin Mode / `DELETE /api/sessions/{id}` runs the 23-step cascade including the inventory decrement — the import never needs a hand-delete.

# Examples

First import, own session `fd47ff62` → `7f2e38b1` (2026-09-01): 1 954 rows — Events 353, Signals/DecisionTransitions/SignalsByKind 390 each, SessionsByStage 307, EventTypeIndex 89, CveIndex 13, AppInstallSummaries 11, 14 inventory counters bumped — in 3 min 10 s; `verify` green (Sessions, mirror, lookup, provenance, zero-leak), `get_session_summary` resolves the new id through `SessionTenantLookup`.

# Citations

- `.claude/commands/import-session.md`, `.claude/commands/scripts/import_session.py` — the skill (submodule)
- `src/Backend/AutopilotMonitor.Functions/Services/Deletion/DeletionManifestBuilder.cs` — the 23-step inventory the import inverts
- `src/Backend/AutopilotMonitor.Functions/Services/Deletion/SessionRestoreService.cs` — the in-product precedent for re-inserting a session's rows
- `src/Backend/AutopilotMonitor.Functions/Services/SessionIndexFieldManifest.cs` — `PrimaryOnly` (the columns that never travel)
- `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/IndexRowKeys.cs`, `Helpers/RowKeyCodec.cs` — key shapes
- [session-annotations.md](session-annotations.md) — provenance lane and the note search
- [lifecycle-manifests-and-session-scope.md](lifecycle-manifests-and-session-scope.md) — why the cascade list is complete
- [../web/demo-presentation-mode.md](../web/demo-presentation-mode.md) — the presentation side
