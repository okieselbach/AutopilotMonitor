---
type: concept
title: Tenant Activation & the Once-Only Welcome Mail
description: How a signup becomes an activated tenant (manual approve or auto-approve queue), and how the welcome mail is sent exactly once even though activation and the user-provided address race each other.
resource: backend
tags: [activation, auto-approve, welcome-email, preview-whitelist, race]
timestamp: 2026-08-23
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

The PUT is `AuthenticatedUserWithRole`, not a member tier, because the signup user is still
roleless until approval auto-promotes them to Admin. The function re-gates on the resolved
role (`PreviewWhitelistFunction.MayWriteNotificationEmailAsync`): a member role or platform
scope always may write; a roleless caller may write only while the tenant has **no enabled
member** at all. Once any member exists the window is closed for good — the address feeds the
welcome and farewell mails and is shown to operators as the tenant contact, so an ordinary
employee with a JWT but no product role must not be able to redirect or clear it (403).

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

## Finding the tenant behind an address

The provider's delivery log names only the recipient — never the tenant it belonged to. The
reverse lookup is therefore part of the tenant console: its search matches `tenantId`,
`domainName`, `ContactEmail` **and** the notification address. No other address is searchable
(an admin UPN never receives platform mail and would only widen the PII surface of the list).

The addresses live in a different table than the tenant configs the list is built from, so the
console loads them as a map from `GET /api/preview/notification-emails` (GA + read-only Global
Reader, `excludeDelegated`) — one cross-partition scan of `RowKey eq 'notification-email'`
instead of a point read per tenant. Keys are lowercased on both sides of the join: the address
rows are written from the JWT `tid` (lowercase) while a config PartitionKey's casing is not
guaranteed. Stored addresses keep the casing the user typed, so matching is case-insensitive.

The map is **best-effort** in the portal: it powers only the search-by-address, so a failed read
degrades that one capability instead of taking the tenant list down. The addresses are not
rendered in the list — they are a search key, and stay visible in the tenant editor.

## Mail transport

Both transactional mails (welcome, post-offboarding farewell) go through the
provider-neutral `EmailService` (`IEmailService` for the welcome path,
`IOffboardFarewellEmailSender` for the farewell path). The provider is an implementation
detail confined to one private transport method plus the `Email:*` configuration
section — a provider swap must not rename classes, DI registrations or callers.

* Current provider: **Mailchimp Transactional (Mandrill)**, `POST {Email:Endpoint}`
  (`messages/send`), API key in the JSON body, typed `HttpClient` with the shared
  `ResiliencePolicies.Notification` retry policy (15 s timeout).
* Settings (App Settings use `Email__Key`): `Email:ApiKey` (required — empty means every
  send is a logged no-op, never an error), `Email:Endpoint`, `Email:FromAddress`
  (default `noreply@autopilotmonitor.com`), `Email:FromName` (default `Autopilot Monitor`).
* Result handling: per-recipient status `sent`/`queued`/`scheduled` = success;
  `rejected`/`invalid` (with `reject_reason`), non-2xx or malformed bodies = warning.
  Never throws — both callers rely on that fail-soft contract.
* `track_opens`/`track_clicks` are explicitly `false` and `auto_text` is `true`: the provider
  receives only the recipient address and the tenant domain, which is exactly the claim on
  the privacy page and the trust data-flows page. Changing tracking = changing those pages.

## Template overrides (operator-editable)

Global Admins can replace either HTML body without a deployment (portal: Settings →
Email Templates). `EmailTemplateService` resolves the body for every send: the stored
override wins, otherwise the built-in `EmailTemplates.*`. Overrides live in the
`PreviewConfig` table (partition `EmailTemplates`, row key `welcome` | `farewell`,
properties `Html`, `UpdatedBy`, `UpdatedUtc`), cached 5 minutes, invalidated on save/reset.

* The only placeholder is `{{domainName}}`; an empty domain renders as "your organization",
  matching the built-ins. Subjects are not editable (pinned constants).
* The domain is HTML-encoded at the point of substitution (`EmailTemplateService.Render` and the
  built-in template builders) — the mail goes out under the product's signed sender to a
  tenant-chosen recipient, so no writer of `DomainName` may ever inject markup into it.
  `DomainName` itself is tenant identity: seeded once at first login from the UPN domain
  (`AuthFunction`, only when it passes `TenantConfigValidation.IsValidDomainName` — a strict
  dotted host name), preserved from the stored row on the full-model config PUT for every
  caller, and "never writable" in the field-patch flow.
* Limit 30 000 characters (Table string property cap is 32K UTF-16 chars).
* The send path is fail-soft: a storage error falls back to the built-in body. The admin
  read path is not — a broken store surfaces as an error instead of silently showing the
  built-in.
* Endpoints (all GlobalAdminOnly): `GET|PUT|DELETE /api/global/email-templates/{kind}` and
  `POST /api/global/email-templates/{kind}/test`. The test send goes to the caller's tenant
  contact address (activation notification email → `ContactEmail` → caller UPN) with the
  caller's tenant domain, optionally with an unsaved draft body; provider tag `test`.

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
* `src/Backend/AutopilotMonitor.Functions/Services/EmailService.cs` — provider-neutral transport (Mandrill wire format, result handling, tracking off)
* `src/Backend/AutopilotMonitor.Functions.Tests/EmailServiceTests.cs` — wire-format, fail-soft and gate-ordering pins
* `src/Backend/AutopilotMonitor.Functions/Services/EmailTemplateService.cs` — override resolution, placeholder rendering, cache
* `src/Backend/AutopilotMonitor.Functions/Functions/Admin/EmailTemplatesFunction.cs` — GA endpoints (read / override / reset / test send)
* `src/Web/autopilot-monitor-web/app/admin/settings/sections/SectionEmailTemplates.tsx` — portal editor, preview frame, test send
* `src/Web/autopilot-monitor-web/app/admin/components/tenantSearch.ts` — the console's match rules (searchable fields, quoted exact mode, lowercased join)
