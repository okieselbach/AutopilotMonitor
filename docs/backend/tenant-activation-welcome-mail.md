---
type: concept
title: Tenant Activation & the Once-Only Welcome Mail
description: How a signup becomes an activated tenant (manual approve or auto-approve queue), and how the welcome mail is sent exactly once even though activation and the user-provided address race each other.
resource: backend
tags: [activation, auto-approve, welcome-email, preview-whitelist, race]
timestamp: 2026-08-02
---

# Schema

## Activation

A tenant is **activated** when the row `PartitionKey={tenantId}, RowKey="approved"` exists
in the `PreviewWhitelist` table (legacy name kept on purpose). Everything else in the
portal gates on that row via `PreviewWhitelistService.IsApprovedAsync` (5 min positive /
30 s negative cache).

Two activation routes, one shared implementation
(`TenantApprovalService.ApproveWithSideEffectsAsync`):

* **Manual**: Global Admin approves in the admin panel (`POST /api/preview/whitelist/{tenantId}`).
* **Auto**: signup unconditionally enqueues a `tenant-auto-approve` envelope; the queue
  worker (`TenantAutoApproveHandler`) re-checks the `AutoApproveNewTenants` flag
  (uncached, fail-closed), the suspension gate (fresh config read), then approves.
  Typical latency: ~1 minute after signup.

The whitelist add is a **conditional INSERT** (`AddEntityAsync`, 409 = lost) — storage
arbitrates concurrent activations; exactly one caller runs the side effects
(TenantAdmin auto-promote of the signup UPN, welcome mail, `TenantAutoApproved` ops event).

## The welcome-mail race

The welcome mail goes to the **notification email** (`RowKey="notification-email"` in the
same table) — the only reliable address: it is typed by the user on the activation-pending
page (`PUT /api/preview/notification-email`). The signup admin UPN may have no mailbox and
`ContactEmail` is merely seeded from the notification email.

That makes activation and address entry concurrent writers. With auto-approve, activation
usually wins (observed in production: approve at T+64 s, address saved at T+69 s → the
approval path found no address and the mail was silently skipped).

**Arbitration — write-then-read on both sides plus a send-once marker:**

* The approval path writes the `approved` row, then reads the address.
* The save path (`SavePreviewNotificationEmail`) writes the address row, then checks
  approval **fresh** (`IsApprovedFreshAsync` — the 30 s negative cache would hide the
  activation for exactly this window).
* Since both do write-then-read, at least one sees both halves and calls
  `TenantApprovalService.TrySendWelcomeEmailAsync`.
* There, a conditional insert of `RowKey="welcome-email-sent"` decides who actually
  sends: at most one wins. The marker is consumed strictly **after** the address check —
  consuming it without an address would permanently suppress the mail.

Marker lifecycle: revoke (`RevokeAsync`) deletes it (re-approve = fresh activation =
fresh mail); the offboarding cascade wipes the whole `PreviewWhitelist` partition anyway.
The Global-Admin resend endpoint (`POST /api/preview/send-welcome-email/{tenantId}`)
always sends (explicit intent) and consumes the marker best-effort so the automatic
paths never duplicate afterwards.

# Examples

* Auto-approve wins the race: approve at T+64 s finds no address → logs "deferred to the
  notification-email save path"; user saves address at T+69 s → save path sees approved
  (fresh read), wins the marker, sends. Response carries `welcomeEmailSent: true`.
* Address saved before manual approve (classic shape): approval path finds the address,
  wins the marker, sends; a later re-save of the same address does not re-send.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Services/TenantApprovalService.cs` — shared activation + `TrySendWelcomeEmailAsync`
* `src/Backend/AutopilotMonitor.Functions/Services/Activation/TenantAutoApproveHandler.cs` — queue-side gates
* `src/Backend/AutopilotMonitor.Functions/Functions/Rules/PreviewWhitelistFunction.cs` — save-path trigger, GA resend
* `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableConfigRepository.cs` — conditional inserts (approved / welcome-email-sent)
* `src/Backend/AutopilotMonitor.Functions.Tests/TenantApprovalServiceTests.cs` — ordering + dedup pins
