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

## Token windows: why a chunk can hold several vectors

Chunking at heading boundaries keeps sections coherent, but it does not bound the
result by *tokens* — and it cannot. Measured on this corpus the ratio ranges from
**2.43 to 5.81 characters per token**, a factor of 2.4, so no character budget maps
reliably onto the model's 256-token window. A budget safe for the worst case
(~620 characters) would shred prose and tables alike.

Left alone, 87 of 324 chunks exceeded the window and **13% of all tokens never
reached the ranking** — silently, since the tokenizer truncates without complaint.
The effect was concrete: in `built-in-rules.md :: Apps` (734 tokens), the strings
`ANALYZE-APP-013`, `ANALYZE-OFFICE-001`, `ANALYZE-CORR-003` and `Click-to-Run` all
sit past the cutoff, so *"proxy configuration blocking app downloads"* did not
retrieve that section at all.

So the *chunk* stays whole and the *embedding* is split instead:
`VectorSearchProvider.index()` cuts long text into 250-token windows with 40 tokens
of overlap and embeds each. A document therefore carries `embeddings: number[][]`,
and `search()` scores it by its **best** window.

- **Max, not mean.** Averaging window vectors would blur a long section's several
  topics into one mediocre direction — the same dilution that makes large context
  windows worse than chunking. Max keeps a document findable by any one thing it
  discusses.
- **The tokenizer is a build-time dependency only.** `index()` runs during the
  Docker build; production boots from precomputed vectors and never loads it. It is
  also only the vocabulary, not the ONNX weights (~93 ms).
- Cost: 324 chunks → 424 vectors (+31%), index 2.63 → 3.12 MB, and 424 instead of
  324 dot products per query — still microseconds.
- The rules corpus benefits too, unplanned: 128 rules now carry 172 vectors.

Measured effect: token coverage 87% → **100%**, *"Office Click-to-Run install never
finished"* 0.318 → **0.442**, *"proxy configuration blocking app downloads"* absent
→ **0.559 at rank 1**. The ten reference queries kept byte-identical scores — their
chunks are short, hence one window, hence the same vector as before.

## Keyword fallback

Semantic ranking alone leaves a gap: proper nouns embed poorly. `RealmJoin` occurs
in 3 chunks yet scores under the threshold.

Fuzzy matching is the wrong remedy, and the corpus says so. At a threshold low
enough to surface `RealmJoin` (0.28), Fuse also returns 5 hits for `SCEPman` — a
word this corpus contains **zero** times. Literal containment has neither problem.

So when the semantic pass returns fewer than `topK`, `search_docs` tops the list up
via `lexicalMatch`, the substring scan every provider already exposes:

- Query terms are filtered to distinctive ones (≥4 characters, non-stopword, max 6).
- A hit needs **corroboration**: at least two matching terms, unless the query has
  only one. Without this rule, "how do I bake sourdough bread" matched a page about
  LLM providers, because "bake" is a substring of "baked into the image".
- Hits are **appended, never merged**: they carry no cosine similarity, so semantic
  results keep the head of the list and keyword hits fill the tail, flagged
  `matchType: "keyword"` with `keywordFallback` in the response.

### When it fires

Two triggers, and the second one exists because the first was not enough:

1. **Fewer than `topK` semantic hits** — the obvious case.
2. **A weak semantic head** (best score below `WEAK_SEMANTIC_SCORE`, 0.35). Topping
   up only a short list misses the more common failure: a pasted identifier like
   `WOW6432Node` returns three chunks at ~0.29 that do not contain the word, while
   the single chunk that does never gets considered. When nothing semantic is
   convincing, exact matches take the front — keeping at least one semantic result,
   so a weak-but-correct hit is never pushed out entirely.

The threshold was chosen by measurement, not intuition. `scripts/eval-docs-search.ts`
scores strategies over 2465 queries in three classes — every chunk heading
(known-item, a regression detector), rare identifiers (the class this fallback
serves), and hand-written questions (the reality anchor, not derived from the
corpus):

| strategy | known-item | literal | handwritten MRR | fixed | broken |
| --- | --- | --- | --- | --- | --- |
| append-only (previous) | 71.9% | 66.2% | 1.000 | — | — |
| weak < 0.30 | 71.9% | 72.6% | 1.000 | 142 | 0 |
| **weak < 0.35** | **73.7%** | **81.1%** | **1.000** | **335** | **0** |
| weak < 0.40 | 76.3% | 88.2% | 0.944 | 502 | 3 |
| weak < 0.45 | 77.2% | 93.0% | 0.889 | 613 | 5 |

0.35 is strictly dominant: better in every class, worse in none. Higher thresholds
keep repairing more identifier queries but start demoting hand-written questions —
the class that most resembles real use — so the gain is not free past that point.

The measurement also corrected the size of the problem: identifier queries were
succeeding only 66.2% of the time, so `WOW6432Node` was not an outlier but one of
roughly 750 such failures.

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
serializes to ~20 characters of JSON, so 384 dims cost ~7.8 KB per vector;
rounding roughly halves the index file, which is `JSON.parse`d on every
scale-to-zero cold start. The induced L2 norm error (~1e-5 over 384 dims) sits
two orders of magnitude inside `UNIT_NORM_EPSILON`.

## Registration

`search_docs` is registered for **every role**, unlike the platform tools: this
is public documentation carrying no tenant data, and a tenant user asking "how do
I deploy the agent" has as much business here as a Global Admin. It is skipped
entirely when the corpus is empty, so a doc-less build advertises no broken tool.

# Cold start

`minReplicas: 0` means every idle period ends in a cold start someone waits through.
Measured from outside: **24.5 s** to first byte after `ScaledToZero`, against **60 ms**
warm.

Where those seconds go could not be answered from outside the process. Log Analytics
stamps every console line of one ingestion batch with the same time, so the boot
sequence collapses to a single instant however it is queried. The server therefore
times itself — `[boot +Nms]` marks from `process.uptime()`, which includes Node's own
startup and module loading.

On a development machine the whole app boot is ~1.3 s:

```
+860ms   node started (startup + module loading)
+1199ms  corpora read from disk        (+339ms)
+1242ms  index read, validated, hydrated (+43ms  <- for 3.12 MB)
+1273ms  listening                      (+31ms)
+1497ms  query embedder warm            (+224ms, background)
```

Two things follow, and both contradict plausible assumptions:

- **Index size is not the cold-start cost.** Parsing and hydrating 3.12 MB takes 43 ms.
  Growing the corpus further is cheap.
- **The prebaked embedder is not either** — 224 ms, and it loads in the background, so
  only the first *search* could ever wait on it.

The dominant local cost is Node startup and module loading. Whether that, or platform
activation (pod scheduling, container start), dominates the production 24.5 s is what
the `[boot]` marks answer on the next cold start. If they show the app listening after
a few seconds, the remainder is platform time that no CPU or index change can touch —
and `minReplicas: 1` would be the only real lever, at roughly 7× the monthly free
consumption grant.

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
* `/src/McpServer/autopilot-monitor-mcp/scripts/eval-docs-search.ts` — retrieval
  evaluation harness; run it before changing anything about ranking
* [MCP OAuth Flow](../mcp-oauth-flow.md) — the other MCP concept document
