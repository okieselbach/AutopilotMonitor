---
type: Concept
title: Ops Notification Channels
description: How platform alerts are routed to named channels instead of three fixed provider slots — the shared NotificationChannel model on both the tenant and platform side, one NotificationChannelDispatcher that routes each channel to its transport, the legacy-slot synthesis that keeps pre-migration dispatch behaviour identical, the empty-means-all rule binding, why Telegram is Global-Admin-only by construction, and why an ops event's structured payload only reaches a destination when a rule opts in.
resource: /src/Backend/AutopilotMonitor.Functions/Services/OpsAlertDispatchService.cs
tags:
  - backend
  - notifications
  - ops-events
  - configuration
  - security
timestamp: 2026-09-01T00:00:00+02:00
---

# Problem

Platform (ops) alerting had no routing. An `OpsAlertRule` was
`{ EventType, MinSeverity, Enabled }` with no destination, and `OpsAlertDispatchService`
broadcast every matching rule to all three hard-wired slots in `AdminConfiguration`
(`OpsAlertTelegramChatId`, `OpsAlertTeamsWebhookUrl`, `OpsAlertSlackWebhookUrl`). Three
consequences followed:

1. **No per-event destinations.** Binding a conversion signal to a sales channel meant every
   other rule reached that channel too.
2. **Only the first matching rule was evaluated** (`FirstOrDefault`), so two rules on the same
   event type could not coexist.
3. **The payload never left the backend.** `OpsEventService.WriteAsync` serialized a `details`
   object into the `OpsEvents` table but passed only `(category, eventType, severity, message,
   tenantId)` to the dispatch — a receiving system saw a tenant GUID and a sentence.

The tenant side already had the right model: `NotificationChannel` (a named list in
`TenantConfiguration.NotificationChannelsJson`) with analyze rules binding channels by id
(`RuleState.NotifyChannelIdsJson`). The platform side needed that model, not a second one.

# Schema

## One model, two scopes

`NotificationChannel` is unchanged and now backs both lists:

| Scope | Storage | Bound by |
| --- | --- | --- |
| Tenant | `TenantConfiguration.NotificationChannelsJson` | per-channel `NotifyOn*` toggles + analyze rules (`NotifyChannelIdsJson`) |
| Platform | `AdminConfiguration.OpsNotificationChannelsJson` | `OpsAlertRule.NotifyChannelIds` |

The scopes stay separate on purpose — platform events resolve against platform channels, and a
tenant's channel list can never receive an ops alert. What is shared is the *model* and the
*dispatch path*, so a new provider is added once.

## One dispatch path

`NotificationChannelDispatcher` is the channel-level send API. It routes by provider:
`WebhookProviderType.Telegram` goes to `TelegramNotificationService`; everything else is a
rendered webhook POST through `WebhookNotificationService`. Every caller that sends to
configured channels — enrollment (`EventIngestProcessor`), session start
(`RegisterSessionFunction`), hardware rejection (`ReportDistressFunction`), SLA
(`SlaBreachEvaluationService`), analyze rules (`AnalyzeOnEnrollmentEndHandler`), ops alerts
(`OpsAlertDispatchService`) and both test endpoints — goes through it.

## Telegram is Global-Admin-only by construction

Telegram (`WebhookProviderType.Telegram = 40`) is the odd provider: it is not a webhook. The
channel's `Url` carries the destination **chat ID**, and the bot token is platform-owned
(PreviewConfig `WebhookUrl`). A tenant admin configuring one would be sending through *our*
bot, which is why the gate is a server-side rule, not a hidden dropdown option:

* `TenantConfigValidation.ValidateTelegramChannelGate` refuses a non-GA caller who **adds** a
  Telegram channel or changes an existing one's destination or enabled state. It lives in
  `ValidateModel`, so it covers both write paths — the full-model PUT *and* the MCP field-patch
  flow (`TenantConfigPatchService`). A check placed in `UpdateTenantConfigurationFunction` alone
  would not.
* An **unchanged** GA-created channel passes, so a tenant admin can still save unrelated config.
* Telegram destinations skip `SsrfGuard.ValidateWebhookUrlFormat` (a chat ID is not a URL and
  would always fail it) and are format-checked as a numeric id or `@username` instead.

## Migration without a data job

`AdminConfiguration.GetOpsNotificationChannels()` returns the stored list, or — when it is empty —
synthesizes one channel per *configured* legacy slot with stable ids (`legacy-telegram`,
`legacy-teams`, `legacy-slack`) and the legacy enabled flags. `GetAdminConfigurationFunction`
projects the same synthesis into the read model, so the editor shows exactly what receives
alerts today and the first save materializes it. The legacy fields stay as the migration source
and are no longer written by the Alerts section.

## Rule binding: empty means all

`OpsAlertRule.NotifyChannelIds` null or empty = every enabled channel. That is what every rule
written before routing existed carries, so behaviour on the deploy day is identical. Dispatch
resolves **all** matching rules (`Where`, not `FirstOrDefault`) and unions their channels;
ids that no longer resolve are dropped rather than falling back to broadcast — a rule whose only
channel was deleted must reach nothing, not everything. The web editor mirrors the same rule in
`app/admin/components/opsChannelRouting.ts`.

## Payload delivery is opt-in per rule

`WriteAsync` hands the serialized `details` to `DispatchAsync`, but a channel only receives it when
a rule sets `OpsAlertRule.IncludePayload`. **Default false**: an ops alert is a "something happened,
go look" signal, most payloads are operational noise in a chat, and some carry data the baseline
never does (tenant domain, administrator contact address). Widening what leaves the platform stays
a deliberate per-rule decision.

`ResolveTargets` therefore returns two groups. Channels reached only by plain rules get an alert
built with `detailsJson: null`; channels reached by an opted-in rule get the enriched one. A channel
in both groups appears in the payload group only — one message per channel, and an unrelated
sibling rule cannot cancel an explicit opt-in.

For the enriched alert, `BuildAlert`:

* flattens top-level **scalars** into `NotificationFact`s — capped at 12 facts and 256 characters,
  nulls and nested values skipped — so card formats and the plain-text Telegram message carry the
  same information,
* passes the raw JSON through `NotificationAlert.DataJson`, which only `GenericJsonRenderer`
  emits, as a real JSON object under `data`. Additive within `schemaVersion` 1.0.

`NotificationAlert.EventType` is set either way: it is the same value the `Event` fact already
carries, so it adds no information — only a stable key for a generic consumer to branch on.

# Examples

A trial-conversion signal reaching only a sales webhook:

1. Admin → Alerts → add a channel `Sales` (Generic JSON, the internal endpoint).
2. Enable the `TenantTrialStarted` rule at `Info`, select **only** `Sales`, and switch its
   **Payload** toggle on (off by default, so every other rule keeps the baseline alert).
3. `PlanManagementFunction` fires `TenantTrialStarted` from both plan write paths — the
   self-service `POST config/{tenantId}/trial` and a GA grant via `PATCH config/{tenantId}/plan`
   (distinguished by `selfService`). Ending a trial is a downgrade and stays with
   `TenantPlanDowngraded`.
4. The webhook receives `eventType`, the rendered facts, and `data` with `domainName`,
   `contactEmail`, `trialStartedUtc`, `trialExpiresUtc`, `grantedBy`, `selfService`. The operator
   push channel receives nothing, because it is not bound to that rule.

# Gotchas

* **A new ops event type must be dual-registered** in `OPS_EVENT_TYPES`
  (`app/admin/components/OpsAlertRulesSection.tsx`) or it cannot be selected. `OpsEventTypeDualRegisterTests`
  enforces it.
* **`OpsNotificationChannelsJson` is secret-bearing** and is on the deny-list in
  `AdminConfiguration.RedactedCopyForReader()`. `NotificationChannel.RedactList` /
  `RestoreRedactedList` are shared with the tenant list — do not add a second copy.
* **Details are capped at 4096 characters** in the `OpsEvents` table (`TableOpsEventRepository`)
  before they ever reach a channel.

# Citations

* `/src/Backend/AutopilotMonitor.Functions/Services/OpsAlertDispatchService.cs`
* `/src/Backend/AutopilotMonitor.Functions/Services/Notifications/NotificationChannelDispatcher.cs`
* `/src/Shared/AutopilotMonitor.Shared/Models/Notifications/NotificationChannel.cs`
* `/src/Shared/AutopilotMonitor.Shared/Models/Config/AdminConfiguration.cs`
* `/src/Backend/AutopilotMonitor.Functions/Helpers/TenantConfigValidation.cs`
* `/src/Backend/AutopilotMonitor.Functions/Functions/Config/PlanManagementFunction.cs`
