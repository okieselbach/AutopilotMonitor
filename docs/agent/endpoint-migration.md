---
type: Concept
title: Agent Endpoint Migration — Config-Channel Re-Home
description: How the backend re-homes agents to a new API base URL (backend move or per-tenant region move) via MigrateToApiBaseUrl on the agent config response, and why HTTP redirects were rejected.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Configuration/EndpointMigration.cs
tags:
  - agent
  - backend
  - config
  - migration
  - security
timestamp: 2026-07-18T00:00:00+02:00
---

# Problem

The agent's API base URL is compiled into the binary (`Constants.ApiBaseUrl`). No DNS/domain
indirection is possible in front of the API: mTLS client-cert auth does not survive a reverse
proxy (Front Door), and Flex Consumption custom-domain TLS is broken. A backend move (like the
2026-07-18 WEU→GWC subscription migration) therefore hard-cuts every deployed agent still
pointing at the old hostname. In-flight agents self-terminate safely (local MaxLifetime watchdog
+ CleanupService), but their sessions are lost and a per-tenant region move (EU→US) has no
mechanism at all.

# Mechanism

`AgentConfigResponse.MigrateToApiBaseUrl` — served on `GET /api/agent/config` (incl. the
bootstrap wrapper), the same control channel as the kill switch, fetched at every agent
process start.

Agent flow (`Program.RunAgent`, Phase 4):

1. Config fetched from the compiled-in (old) base URL.
2. Kill-switch check runs FIRST — **kill wins over migrate**.
3. `AgentRuntimeConfig.Resolve` detects the migration signal directly after the fetch and
   returns early, BEFORE merge / binary-integrity / bootstrap-cleanup — none of those side
   effects may run against a config from the backend being abandoned (a migration stub may
   advertise stale agent hashes; a spurious `IntegrityCheckFailed` + force-update would be
   harmful).
4. `Program.RunAgent` swaps `AgentConfiguration.ApiBaseUrl`, disposes the old client stack,
   rebuilds Phase 3 (auth clients) and re-runs Phase 4 against the new URL —
   `allowEndpointMigration: false`, so the new backend's config **cannot chain a second hop**.
5. Kill-switch check runs again on the NEW backend's config; startup continues normally
   (Phase 5+ builds mTLS/telemetry clients from the updated base URL).

# Hardening (mirrors the kill switch)

* **Live fetch only** — the on-disk config cache strips `MigrateToApiBaseUrl` on write AND
  read (`RemoteConfigService.CacheConfig` / `LoadCachedConfig`); `EndpointMigration.ResolveTarget`
  re-checks `LastFetchOutcome == Succeeded` as defence-in-depth. A planted cache file can
  never re-home an agent.
* **Allowlist validated on BOTH sides** — `AgentEndpointMigrationRules` (Shared, single
  source): https only, host must end in `.azurewebsites.net` on a label boundary, no
  path/query/userinfo/port. The backend refuses to serve an invalid admin value
  (`AgentMigrateRejected` warning); the agent refuses to honour one even if the backend was
  compromised.
* **One hop, no loops** — second Resolve pass never migrates; a target equal to the current
  base URL is a no-op (steady state during the migration window).

# Operation

Global admin settings (`AdminConfiguration`, admin portal → Global Settings):

* `AgentMigrateApiBaseUrl` — global target, set on the backend being **abandoned**; clear it
  after the migration window.
* `AgentMigrateTenantOverridesJson` — JSON object `tenantId → URL` for per-tenant moves.
  An entry with an **empty string** pins that tenant against the global target (staged rollout).
  Overrides win over the global value.

Delivery evidence: every served target logs `AgentMigrateServed` (Warning — reaches App
Insights) with tenant, serial, agent version and target. Agent-side, the re-home is logged as
`Endpoint migration: backend re-homed this agent from … to …`.

Reach limitation: agents that cannot complete an authenticated config fetch (e.g. cert-broken
devices) never see the signal — they fall back to the local MaxLifetime/emergency-break
lifecycle. During a migration window the old backend must keep running as a serving stub;
keeping the old `*.azurewebsites.net` name parked afterwards also prevents hostname
re-registration by third parties while stragglers drain.

# Citations

* `src/Shared/AutopilotMonitor.Shared/Services/AgentEndpointMigrationRules.cs` — shared validation rules
* `src/Shared/AutopilotMonitor.Shared/Models/Config/AgentConfigResponse.cs` — wire field
* `src/Backend/AutopilotMonitor.Functions/Functions/Config/GetAgentConfigFunction.cs` — `ResolveMigrateTarget` + serving (ConfigVersion 33)
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Configuration/EndpointMigration.cs` — agent-side evaluation
* `src/Agent/AutopilotMonitor.Agent.V2/Runtime/AgentRuntimeConfig.cs` — early return before merge/integrity
* `src/Agent/AutopilotMonitor.Agent.V2/Program.cs` — one-hop rebuild in `RunAgent`
* Tests: `AgentEndpointMigrationTests` (backend), `EndpointMigrationTests` + `RemoteConfigServiceCacheHardeningTests` (agent)
