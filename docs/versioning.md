---
type: Concept
title: Version Contract — Agent, Backend, MCP
description: One version shape for all three shipping components, the ETag-CAS counter blob that mints build numbers, and the published manifests that make "what is deployed" checkable from outside.
resource: /.github/scripts/Request-BuildNumber.ps1
tags:
  - build
  - ci
  - release
  - versioning
timestamp: 2026-07-22T00:00:00+02:00
---

# Version Contract

Three components ship from this repository — the agent, the Functions backend and the
MCP server. They share one version shape, one way of minting build numbers, and one
manifest format, so that a version string means the same thing whoever emits it.

```
version   = <major>.<minor>.<build>    major.minor curated by hand, build minted per build
commit    = 7-char short SHA of this repo
buildUtc  = ISO-8601 build moment
```

| Component | major.minor from | Counter blob | Reported by |
|---|---|---|---|
| Agent V2 | `VersionPrefix` in `AutopilotMonitor.Agent.V2.csproj` | `agent/buildcounter-v2.txt` | `agent/version.json` |
| Backend | `VersionPrefix` in `AutopilotMonitor.Functions.csproj` | `versions/backend.txt` | `GET /api/health` |
| MCP | `version` in `package.json` | `versions/mcp.txt` | `GET /health`, MCP handshake |

# Schema

## Build numbers — reserve before build, ETag-CAS

`.github/scripts/Request-BuildNumber.ps1` is the only implementation; the CI
workflows and the local `build.ps1` all call it.

1. Anonymous `GET` on the counter blob → value `N` and `ETag`. The blob holds the
   **last used** number as plain text and is public-read; only the write needs the
   container SAS.
2. `PUT N+1` with `If-Match: <etag>`. The quoted ETag form is required — PowerShell
   strips the quotes on read, and an unquoted `If-Match` is rejected client-side.
3. `412 Precondition Failed` → a concurrent build reserved first → re-read and retry
   (5 attempts, linear backoff).
4. Build with the reserved number.

Reserving *before* the build is what makes concurrent minting impossible. **A failed
build burns a number** — deliberate: uniqueness matters, density does not.

Why a counter and not the published manifest: manifests are written on publish, not on
build. The agent writes `version.json` only for stable releases, so deriving the next
number from it would hand two consecutive dev builds the same number and let the second
overwrite the first's artifact. A state-free scheme such as `git rev-list --count HEAD`
fails for the same reason — two builds of one commit collide.

**Seeding** (once per component): `PUT <last used number>` with `If-None-Match: *`, so
an existing counter can never be clobbered. Agent V2 was seeded with `1356` on
2026-07-17; `versions/backend.txt` with `999` and `versions/mcp.txt` with `299` on
2026-07-22.

## Local builds

No counter is reached, the build number stays `0`, and the commit is empty or
`unknown`. A `1.5.0` with no commit hash on `/api/health` is therefore a developer
machine, not a release — the fallback is the signal, not a defect. The agent is the
exception: `build.ps1` produces uploadable artifacts and reserves a real number unless
`-SkipUpload` is passed.

## Manifests

`versions/backend.json` and `versions/mcp.json` (public-read) record what is deployed:

```json
{ "component": "backend", "version": "1.5.1000", "commit": "abc1234",
  "buildUtc": "…", "deployedUtc": "…", "runId": "…" }
```

The MCP manifest adds `docsCommit`, because for that component "which build" and "which
documentation" are separate questions.

Each is written **last and only after the deploy workflow has polled the component's
health endpoint until it reports that exact version and commit**. A green deploy step
alone means the package was accepted, not that it is being served. Consequently a
manifest that disagrees with the live endpoint means the running instance drifted after
the fact (rollback, stale revision) rather than a half-finished pipeline.

The agent keeps its own manifest at `agent/version.json` — the bootstrap derives that
URL from the download URL and verifies the ZIP against its `sha256`, so it cannot move.
It carries the same `commit` and `buildUtc` fields; `sha256` and `bootstrapVersion`
remain, being the customer-facing integrity contract.

# Consequences

- Bumping `major.minor` is a human decision, made in the component's own file
  (`VersionPrefix`, or `version` in `package.json`). Nothing bumps it automatically.
- A new component joins by seeding a counter blob and calling the same script; no new
  mechanism.
- The counter blobs and manifests live in the `versions` container on
  `autopilotmonitoreu` (writes via `AZURE_BLOB_VERSIONS_SAS_TOKEN`). The agent's
  counter stays in the `agent` container next to the artifacts it numbers.

# Citations

- `.github/scripts/Request-BuildNumber.ps1` — the CAS protocol
- `.github/workflows/deploy-backend.yml`, `deploy-mcp.yml` — reserve, verify, publish
- `.github/workflows/build-agent.yml`, `scripts/Deployment/build.ps1` — agent line
- [Build Counter Blob](agent/build-counter-blob.md) — agent-specific consequences
