---
okf_version: "0.1"
---

# Autopilot Monitor — Technical Knowledge Bundle

Contributor-facing technical documentation for Autopilot Monitor, organized as an
[Open Knowledge Format (OKF)](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
knowledge bundle. Customer-facing product documentation lives at
https://docs.autopilotmonitor.com (separate repository).

# Architecture

* [Architecture Guide](architecture.md) - High-level architecture overview and solution layout for contributors.

# Agent

* [V2 Agent](agent/index.md) - How the on-device agent works: [runtime overview](agent/overview.md), [decision engine](agent/decision-engine.md) (reducer, signals, completion arms), [logs & persistence](agent/logs-and-persistence.md) (signal log, snapshot, crash recovery), [Hello wizard un-skip](agent/hello-wizard-unskip.md) (why a single "WHfB disabled" read never decides completion), [Autopilot ZTD diagnostics](agent/autopilot-ztd-diagnostics.md) (event IDs, diagnostic registry, endpoints, error-code map).
* [Build Counter Blob](agent/build-counter-blob.md) - How agent build numbers are minted: shared counter blob with ETag-CAS so local and CI builds never collide; CI additionally attests build provenance (Sigstore).
* [Agent Endpoint Migration](agent/endpoint-migration.md) - How the backend re-homes agents to a new API base URL via the config channel (backend move or per-tenant region move): live-fetch-only, allowlist-validated on both sides, one hop, kill wins.
* [MDM Reboot Coalescing](agent/mdm-reboot-coalescing.md) - Attributing the mid-ESP coalesced reboot ("second sign-in") to the device-assigned policy URIs that forced it: aggregated DM-Enterprise 2800 observations (neutral, flush-time watermark) + the ANALYZE-ESP-005 advisory gated on an actually-observed reboot.

# Rules

* [Gather Rule Phase Scoping & Emit-on-Change](rules/gather-rule-phase-scoping.md) - Restricting gather rules to enrollment phases (activePhases / activeFromPhase sticky latch) and emitMode "on_change" result-dedup — the anti-spam pair for interval rules.

# Backend

* [Business Timestamps](backend/business-timestamps.md) - Why the Azure Tables system Timestamp is never authoritative (migrations reset it), and the OccurredUtc + RowKey-decode compensation for AuditLogs/OpsEvents/Events after the 2026-07-18 migration.

# Security & Identity

* [MCP OAuth Flow](mcp-oauth-flow.md) - Who authenticates where when connecting an AI client to the MCP server; two identities, three parties plus Entra ID.

# Conventions for this bundle

* Every concept document is a markdown file with YAML frontmatter; `type` is mandatory, `title`, `description`, `resource`, `tags`, and `timestamp` are recommended.
* Use standard **relative** markdown links between documents (e.g. `agent/overview.md`, `../architecture.md`). Do NOT use the spec's `/`-prefixed bundle-absolute form — GitHub resolves those against the repo root (missing `/docs`) and navigation breaks.
* Record notable additions and changes in [log.md](log.md).
* `index.md` and `log.md` are reserved names — never use them for concept documents.
