---
type: Concept
title: MCP Protocol Eras & Client ID Metadata Documents
description: How the MCP server serves the 2026-07-28 stateless revision and 2025-era clients from one per-request factory, why the legacy leg is hand-wired (JSON + gzip), the cache-hint and capability contract, and the CIMD client-registration path next to the deprecated dynamic registration.
resource: /src/McpServer/autopilot-monitor-mcp/src/mcp-http.ts
tags:
  - mcp
  - protocol
  - oauth
  - cimd
  - security
timestamp: 2026-08-29T08:40:00+02:00
---

# MCP Protocol Eras & Client ID Metadata Documents

**Status:** Concept · **Owner:** MCP Server · **Code:** [`mcp-http.ts`](../../src/McpServer/autopilot-monitor-mcp/src/mcp-http.ts), [`mcp-server-factory.ts`](../../src/McpServer/autopilot-monitor-mcp/src/mcp-server-factory.ts), [`cimd.ts`](../../src/McpServer/autopilot-monitor-mcp/src/cimd.ts), [`oauth.ts`](../../src/McpServer/autopilot-monitor-mcp/src/oauth.ts)

# Schema

## The two eras

MCP revision **2026-07-28** turned the protocol into a stateless request/response
protocol: no `initialize` handshake, no `Mcp-Session-Id`, every request carries its
protocol version, client identity and capabilities in `_meta`, capabilities and
instructions are fetched with `server/discover`, list results carry `ttlMs` /
`cacheScope`, and `Mcp-Method` / `Mcp-Name` HTTP headers let gateways route without
parsing bodies. Roots, Sampling, Logging and the HTTP+SSE transport are deprecated
(twelve-month window); Dynamic Client Registration is deprecated in favour of Client
ID Metadata Documents.

The server was already stateless by design (scale-to-zero Container App, no
server→client traffic), so the revision cost no architecture — only wiring:

| Concern | 2026-07-28 ("modern") | 2025-era ("legacy") |
| --- | --- | --- |
| Routing decision | `isLegacyRequest()` from the SDK — the exact classifier `createMcpHandler` uses internally | same predicate, inverse |
| Serving | `createMcpHandler(factory, { legacy: 'reject', responseMode: 'json' })` via `toNodeHandler` | fresh `NodeStreamableHTTPServerTransport({ sessionIdGenerator: undefined, enableJsonResponse: true })` per POST |
| Response shape | single `application/json` body | single `application/json` body |
| Instructions | `server/discover` result | `initialize` result |
| Cache fields | `ttlMs` + `cacheScope` stamped by the SDK from `cacheHints` | none (2025 codec has no cache path) |

Both legs are fed by **one** factory (`createServerForCaller`), so the tool catalog,
resource set, prompts and instructions can never drift between eras — the
`mcp-http.test.ts` suite pins that a Global Admin and a plain tenant user see the
same catalog on either path.

## Why the legacy leg is hand-wired

`createMcpHandler`'s built-in `legacy: 'stateless'` fallback constructs its transport
with only `sessionIdGenerator: undefined`, i.e. it answers every 2025 client with an
SSE frame. Every production client today (Claude Code, Claude.ai, VS Code) is a 2025
client, and their large tool results are gzipped by the compression middleware only
because the response is a plain JSON body (measured 5–30×). Routing on the SDK's own
predicate in user land keeps that behaviour while still running the 2026 path through
the SDK entry unchanged — no re-implementation of the classification, no second
catalog.

## Capability and cache contract

* `capabilities` are declared explicitly with `listChanged: false` for tools,
  resources and prompts. The SDK's default (`?? true`) would advertise a
  notification a per-request server can never emit.
* `cacheHints`: `server/discover`, `tools/list`, `prompts/list`, `resources/list`,
  `resources/templates/list` → `{ ttlMs: 300_000, cacheScope: 'private' }`;
  `resources/read` → `{ ttlMs: 3_600_000, cacheScope: 'private' }`. **Never
  `public`**: the catalog and the instructions are role-dependent (Global Admin /
  Global Reader / delegated MSP / tenant user), so a shared cache would leak one
  caller's surface to another. Five minutes is the bound on how long a role change
  can go unnoticed by a caching client; resources are static per deployment.
* No Roots / Sampling / Logging / elicitation / subscriptions are used anywhere, so
  nothing on the deprecation list has to be migrated.

## Client registration: CIMD first, DCR as fallback

The authorization-server metadata now advertises
`client_id_metadata_document_supported: true` next to the (deprecated)
`registration_endpoint`. Clients following the spec's priority order use a
**Client ID Metadata Document**: their `client_id` *is* an HTTPS URL that serves a
JSON document (`client_id`, `client_name`, `redirect_uris`, …). `oauth.ts` resolves
every `client_id` through one helper: an HTTPS URL goes to `cimd.ts`, anything else
must be one of our HMAC-signed dynamic-registration tokens. Both yield the same shape
(`redirectUris`), and the rest of the authorize/callback logic is unchanged.

What the document decides — and what it does not:

* It only asserts **which** redirect URIs the client claims. The requested
  `redirect_uri` must still pass the host/path allowlist that gates dynamic
  registration (`isAllowedRedirectUri`), checked *before* the document is fetched. A
  self-hosted document cannot widen the destinations an authorization code may be
  sent to. Loopback remains allowed for every client (RFC 8252 §7.3);
  `application_type: "native"` is informational.
* Fetching a caller-chosen URL is an SSRF surface, so the fetch is: HTTPS only, path
  component required, no fragment/userinfo, no loopback or IP-literal host, every
  resolved address must be public (RFC 1918/4193/3927/6598, CGNAT, multicast,
  IPv4-mapped all refused — checked before any request leaves), `redirect: 'error'`,
  5 s budget, 16 KB body cap (declared *and* streamed), JSON media type required.
  Nothing from the response reaches the caller beyond an OAuth error code.
* The document's `client_id` must equal the URL by simple string comparison — no
  normalization, per the draft. `redirect_uris` is bounded exactly like a dynamic
  registration (`oauth-limits.ts`). A missing `client_name` falls back to the host:
  it only ever reaches a log line, and failing a login over a label would be wrong.
* Cache: positive entries live for the document's `Cache-Control: max-age`, clamped
  to 10–60 min so the `/oauth/callback` re-check within the 10-min signed-state
  window is a cache hit on the same replica; rejections are cached for 60 s; the map
  is bounded to 256 entries.

RFC 9207 `iss` (already emitted since 2026-07-29) completes the authorization side
of the 2026-07-28 revision.

# Examples

* A 2026 client: `POST /mcp` with `_meta["io.modelcontextprotocol/protocolVersion"] = "2026-07-28"`
  and `Mcp-Method: tools/list` → `{ tools: [...], ttlMs: 300000, cacheScope: "private", _meta: { "io.modelcontextprotocol/serverInfo": { name: "Autopilot-Monitor", version: "1.6.<build>" } } }`.
* A 2025 client: `POST /mcp` `initialize` → `application/json` body with
  `protocolVersion: "2025-11-25"` and the role-tailored `instructions`; no
  `Mcp-Session-Id` header.
* `GET /oauth/authorize?client_id=https://app.example.test/oauth/client.json&redirect_uri=http://127.0.0.1:49152/callback&code_challenge=…` →
  document fetched (or served from cache) → 302 to Entra. `client_id=https://10.0.0.8/x` → `400 invalid_client`
  without any outbound request.

# Citations

* MCP spec 2026-07-28 — https://blog.modelcontextprotocol.io/posts/2026-07-28/ ; authorization / client registration:
  https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/client-registration
* TypeScript SDK v2 (`@modelcontextprotocol/server` 2.0.0) — `createMcpHandler`, `isLegacyRequest`, `cacheHints`.
* draft-ietf-oauth-client-id-metadata-document-00 §6 (security considerations, SSRF).
* Tests: `src/__tests__/mcp-http.test.ts`, `src/__tests__/cimd.test.ts`, `src/__tests__/oauth-cimd.test.ts`.
* Related: [MCP OAuth Flow](../mcp-oauth-flow.md), [MCP Docs Corpus](docs-corpus.md).
