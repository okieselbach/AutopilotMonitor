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
* [Version Contract](versioning.md) - One version shape for agent, backend and MCP: the ETag-CAS counter blob that mints build numbers, and the manifests published only after a deploy is verified live.
* [URL Registry](url-registry.md) - Every well-known host lives in one registry file per component (Constants.cs / utils/config.ts / src/config.ts), enforced by guard tests; includes the agent-download migration to the download alias and its deploy-order constraint.

# Agent

* [V2 Agent](agent/index.md) - How the on-device agent works: [runtime overview](agent/overview.md), [decision engine](agent/decision-engine.md) (reducer, signals, completion arms), [logs & persistence](agent/logs-and-persistence.md) (signal log, snapshot, crash recovery), [Hello wizard un-skip](agent/hello-wizard-unskip.md) (why a single "WHfB disabled" read never decides completion), [Autopilot ZTD diagnostics](agent/autopilot-ztd-diagnostics.md) (event IDs, diagnostic registry, endpoints, error-code map).
* [Build Counter Blob](agent/build-counter-blob.md) - How agent build numbers are minted: shared counter blob with ETag-CAS so local and CI builds never collide; CI additionally attests build provenance (Sigstore).
* [Agent Endpoint Migration](agent/endpoint-migration.md) - How the backend re-homes agents to a new API base URL via the config channel (backend move or per-tenant region move): live-fetch-only, allowlist-validated on both sides, one hop, kill wins.
* [MDM Reboot Coalescing](agent/mdm-reboot-coalescing.md) - Attributing the mid-ESP coalesced reboot ("second sign-in") to the device-assigned policy URIs that forced it: aggregated DM-Enterprise 2800 observations (neutral, flush-time watermark) + the ANALYZE-ESP-005 advisory gated on an actually-observed reboot.
* [Secure Boot CA-2023 Detection](agent/secureboot-ca2023-detection.md) - Why ANALYZE-SEC-001 v3 reads the UEFI db/KEK variables directly instead of trusting the SecureBoot\Servicing registry, and how the one-sided uefiCa2023FirmwareConfirmed marker plus a not_exists precondition give absence-tolerant suppression across old and new agents.
* [ESP Policy-Provider Stall Detection](agent/esp-policy-provider-stall.md) - The EnrollmentStatusTracking CSP's no-timeout Setup/Apps wait is keyed to the Intune-registered "Sidecar" provider by NAME: co-management's "ConfigMgr" parks the user ESP at "Apps (Identifying)" for days even with TrackingPoliciesCreated=1 (issue #106); the always-on two-arm esp_policy_provider_stalled tripwire (provider_incomplete | sidecar_provider_missing) and the SOFTWARE\Microsoft\Windows\Autopilot gather-rule allowlist entry that surface it.

# Rules

* [Gather Rule Phase Scoping, One-Shot Triggers & Emit-on-Change](rules/gather-rule-phase-scoping.md) - Restricting gather rules to enrollment phases (activePhases / activeFromPhase sticky latch), one-shot collection at a phase's start/end (phase_change / phase_exit), and emitMode "on_change" result-dedup.
* [Gather Rule Guardrails](rules/gather-rule-guardrails.md) - Why the allowlists in rules/guardrails.json are enforced on the agent and nowhere else, which hard blocks survive unrestricted mode, and the rule that every collector must call a guard and emit security_warning on refusal.

# Backend

* [Business Timestamps](backend/business-timestamps.md) - Why the Azure Tables system Timestamp is never authoritative (migrations reset it), and the OccurredUtc + RowKey-decode compensation for AuditLogs/OpsEvents/Events after the 2026-07-18 migration.

# MCP Server

* [MCP Docs Corpus](mcp/docs-corpus.md) - How the published customer documentation becomes the `search_docs` corpus: CRLF-safe heading chunking, build-time embedding as a third precomputed section, and why a docs edit needs an MCP redeploy.

# Security & Identity

* [MCP OAuth Flow](mcp-oauth-flow.md) - Who authenticates where when connecting an AI client to the MCP server; two identities, three parties plus Entra ID.

# Conventions for this bundle

* Every concept document is a markdown file with YAML frontmatter; `type` is mandatory, `title`, `description`, `resource`, `tags`, and `timestamp` are recommended.
* Use standard **relative** markdown links between documents (e.g. `agent/overview.md`, `../architecture.md`). Do NOT use the spec's `/`-prefixed bundle-absolute form — GitHub resolves those against the repo root (missing `/docs`) and navigation breaks.
* Record notable additions and changes in [log.md](log.md).
* `index.md` and `log.md` are reserved names — never use them for concept documents.
