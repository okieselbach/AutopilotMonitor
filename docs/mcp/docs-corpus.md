---
type: Concept
title: MCP Docs Corpus — search_docs and the Cross-Repo Build Coupling
description: How the published customer documentation becomes a third semantic search corpus in the MCP server — CRLF-safe heading chunking, build-time embedding, the corpus hash guard, and why editing the docs does not update a running server.
resource: /src/McpServer/autopilot-monitor-mcp/src/docs-corpus.ts
tags:
  - mcp
  - search
  - embeddings
  - documentation
  - build
timestamp: 2026-07-21T19:30:00+02:00
---

# Purpose

The MCP server answers two different kinds of question, and they need two
different corpora.

* *"Why did this enrollment fail?"* → analysis rules, gather rules, IME log
  patterns. That is `search_knowledge`, backed by `knowledge-base.ts`.
* *"How do I deploy the agent?" / "Where is my data stored?"* → the published
  customer documentation. That is `search_docs`, backed by `docs-corpus.ts`.

They are deliberately **separate indexes**. Merged into one, they would compete
for the same `topK` and dilute each other: a rules query would surface prose
pages, and a product question would surface rule definitions.

# Schema

## Corpus source

The corpus is the `autopilotmonitor-docs` repository — a public OKF bundle
published at `docs.autopilotmonitor.com`. It is **not** vendored into this repo.
The deploy workflow checks it out into `mcp-docs/` of the Docker build context;
the Dockerfile copies it to `/app/docs` in both the build and the final stage
(byte-identical, or the boot-time corpus hash rejects the precomputed index).

`SUMMARY.md` and `log.md` are skipped as navigation scaffolding. `index.md` is
kept — it is the documentation map and answers "what documentation exists" — but
gets no `url`, because the bundle deliberately keeps it out of `SUMMARY.md` and
GitBook therefore never renders it.

## Chunking

A whole page cannot be one document: `trust/security-faq.md` is 36 KB, while
MiniLM-L6-v2 truncates at ~256 tokens. An un-chunked page would silently embed
only its first paragraph.

Chunks are cut at **h2/h3 headings**, which in this bundle are also the semantic
boundaries — `security-faq.md` carries 37 h3s, one per question.
`<details><summary>Q</summary>` blocks (used by `troubleshooting/faq.md`) are
treated the same way, with the summary as the section title. Each chunk is
prefixed with a `Page Title › Section › Subsection` breadcrumb so a hit lifted
out of the middle of a page is self-describing to both the embedder and the
reader.

Two deliberate exceptions to the ~1500-character budget:

* **Markdown tables are never split.** A table broken across chunks leaves
  `| --- |` rows with no header — unreadable in a result and meaningless to
  embed. Tables carry real answers here (the role matrix, the settings
  reference), so an over-long table is allowed to exceed the budget.
* **Unbroken lists split at line level.** The changelog pages are one bullet
  list per release with no blank lines, so paragraph splitting cannot touch
  them; left whole, everything past the model's window would be truncated away
  and become unsearchable.

Short sections are **kept**, not filtered. A one-line answer ("Is it free?" →
"Yes, the Community plan is free") is often the most valuable hit, and the
breadcrumb supplies the context the short body lacks. Only structural residue
(under 25 alphanumeric characters) is dropped.

## CRLF is a correctness precondition

The docs repo is authored on Windows and ships CRLF. In JavaScript `.` does not
match `\r`, so a pattern like `/^(#{2,3})\s+(.*)$/` matches **nothing** on a CRLF
file: `(.*)` stops before the `\r` and `$` never reaches end-of-string.

This was observed during development, and the failure mode is what makes it
dangerous: no crash, no error — headings and frontmatter simply stop being
recognized and every page silently degrades to unlabelled paragraph splits. The
first working build produced 286 plausible-looking chunks that were almost all
wrong; after normalizing, `security-faq.md` went from 29 blind splits to 46 real
question sections.

`normalizeNewlines()` runs before any parsing. Do not remove it, and do not
"fix" a future parsing bug here by adding `\r?` to one pattern.

## Build-time embedding

Like the rules corpus, docs are embedded once at Docker build time by
`precompute-embeddings.js <rulesDir> <outFile> [docsDir]`, into a third
`PrecomputedSection` (`docs`) of `search-index.json`. The container never embeds
a corpus at boot — only the incoming query.

The `docs` section is **optional** in the file format, which is what makes a
local checkout without the docs repo work: `validatePrecomputedIndex` skips it
when the booting server loaded zero doc chunks. The asymmetry is intentional —
if the server *has* a docs corpus and the file does *not*, that is staleness and
is rejected like any other mismatch.

Embeddings are rounded to 6 decimals on serialize. A float64 component
serializes to ~20 characters of JSON, so 384 dims cost ~7.8 KB per document;
rounding roughly halves the index file, which is `JSON.parse`d on every
scale-to-zero cold start. The induced L2 norm error (~1e-5 over 384 dims) sits
two orders of magnitude inside `UNIT_NORM_EPSILON`.

## Registration

`search_docs` is registered for **every role**, unlike the platform tools: this
is public documentation carrying no tenant data, and a tenant user asking "how do
I deploy the agent" has as much business here as a Global Admin. It is skipped
entirely when the corpus is empty, so a doc-less build advertises no broken tool.

# Cross-repo coupling

**Editing the documentation does not update a running MCP server.** The corpus is
baked into the image, so only re-running the *Deploy MCP Server* workflow picks
up doc changes.

That coupling is made checkable rather than memorable: the build passes the docs
checkout SHA as `--build-arg DOCS_COMMIT`, and `/health` reports
`docs: { commit, chunks, sections }`. Comparing that commit against
`git rev-parse HEAD` in `autopilotmonitor-docs` answers "is the deployed
documentation stale?" directly.

Automatic redeploy on a docs push was considered and rejected: it would need a
PAT secret in the docs repo and would turn every typo fix into a container
deployment.

# Examples

Retrieval measured against the real bundle (323 chunks, all-MiniLM-L6-v2):

```
"where is my data stored"            → 0.566  troubleshooting/faq.md :: General › Where is my data stored?
"how do I deploy the agent with Intune" → 0.819  troubleshooting/faq.md :: Setup & Agent › How do I deploy the agent?
"which roles exist and what can they do" → 0.500  trust/security-faq.md :: Identity and Access › What roles exist?
"how do I set up Teams notifications"    → 0.592  reference/settings.md  :: Tenant › Notifications
"what firewall URLs do I need to allow"  → 0.374  reference/network-endpoints.md :: (intro)
```

The 0.25 `minScore` default is tuned to that observed band — short keyword
queries on all-MiniLM score low, and a relevant-but-marginal hit lands around
0.34.

# Citations

* `/src/McpServer/autopilot-monitor-mcp/src/docs-corpus.ts` — loader, frontmatter
  parser, chunker
* `/src/McpServer/autopilot-monitor-mcp/src/tools/search.ts` — `search_docs`
  registration
* `/src/McpServer/autopilot-monitor-mcp/src/precomputed-index.ts` — `docs`
  section and its validation asymmetry
* `/src/McpServer/autopilot-monitor-mcp/Dockerfile` — `mcp-docs/` copy,
  `DOCS_COMMIT`
* `/.github/workflows/deploy-mcp.yml` — docs checkout and SHA capture
* [MCP OAuth Flow](../mcp-oauth-flow.md) — the other MCP concept document
