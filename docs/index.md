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

# Security & Identity

* [MCP OAuth Flow](mcp-oauth-flow.md) - Who authenticates where when connecting an AI client to the MCP server; two identities, three parties plus Entra ID.

# Conventions for this bundle

* Every concept document is a markdown file with YAML frontmatter; `type` is mandatory, `title`, `description`, `resource`, `tags`, and `timestamp` are recommended.
* Use standard **relative** markdown links between documents (e.g. `agent/overview.md`, `../architecture.md`). Do NOT use the spec's `/`-prefixed bundle-absolute form — GitHub resolves those against the repo root (missing `/docs`) and navigation breaks.
* Record notable additions and changes in [log.md](log.md).
* `index.md` and `log.md` are reserved names — never use them for concept documents.
