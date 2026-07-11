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

# Security & Identity

* [MCP OAuth Flow](mcp-oauth-flow.md) - Who authenticates where when connecting an AI client to the MCP server; two identities, three parties plus Entra ID.
* [Optional Graph add-on permissions](customer-graph-add-on-permissions.md) - Opt-in tenant-side Graph permission grants via appRoleAssignment, without changing the published app manifest.

# Conventions for this bundle

* Every concept document is a markdown file with YAML frontmatter; `type` is mandatory, `title`, `description`, `resource`, `tags`, and `timestamp` are recommended.
* Prefer bundle-relative links starting with `/` (interpreted from `docs/`) so links survive moves.
* Record notable additions and changes in [log.md](log.md).
* `index.md` and `log.md` are reserved names — never use them for concept documents.
