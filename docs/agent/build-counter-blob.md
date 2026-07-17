---
type: Concept
title: Agent Build Numbers — Shared Counter Blob with ETag-CAS
description: Why agent build numbers come from a public counter blob reserved before the build, how the compare-and-swap protocol works, and what the repo's buildcounter.txt still does.
resource: /.github/workflows/build-agent.yml
tags:
  - agent
  - build
  - ci
  - release
  - versioning
timestamp: 2026-07-17T00:00:00+02:00
---

# Agent Build Numbers — Shared Counter Blob

Agent (V2 line) build numbers — the `<n>` in `2.0.<n>` — are minted from a single
shared counter blob so that **local builds (`scripts/Deployment/build.ps1`) and CI
builds (`.github/workflows/build-agent.yml`) can never collide**. Both build paths
stay first-class: local daily-ops builds remain possible, CI builds add provenance
attestation on top.

# Schema

- **Counter blob**: `https://autopilotmonitor.blob.core.windows.net/agent/buildcounter-v{LINE}.txt`
  (per release line, analogous to `version-v{LINE}.json`). Content: the **last used**
  build number as plain text. Public-read like the rest of the container (the number
  is visible in `version-v{LINE}.json` anyway); writes require the container SAS.
- **Protocol — reserve before build, ETag-CAS**:
  1. Anonymous `GET` the blob, capture value `N` and the `ETag`.
  2. `PUT N+1` with `If-Match: <etag>` (quoted form — pwsh strips the quotes on read,
     they must be re-added or the request is rejected client-side).
  3. `412 Precondition Failed` → another build reserved concurrently → re-read, retry
     (5 attempts, linear backoff).
  4. Build with `BUILD_NUMBER=N+1` in the environment. The csproj honors
     `BUILD_NUMBER` and then skips its `PersistBuildCounter` target, so the repo file
     is not touched.
- **Failed builds burn a number.** That is intentional: uniqueness matters, density
  does not. Reserve-before-build is what makes concurrent minting impossible.
- **CI manual override** (`build_number` input): numbers `<=` counter are re-builds
  and leave the blob unchanged; numbers `>` counter advance it (monotonic guard).
- **Seeding** (one-time per line): `PUT <last used number>` with `If-None-Match: *`
  so an existing counter can never be clobbered. V2 was seeded with `1356` on
  2026-07-17.

# Consequences

- `src/Agent/AutopilotMonitor.Agent.V2/buildcounter.txt` is now only an **offline
  fallback** for `-SkipUpload` smoke builds and bare `dotnet build` (where the version
  does not matter). It is never committed anymore; the CI step that pushed counter
  bumps to `main` was removed.
- `AdminConfiguration.LatestAgentV2*` (backend hash-oracle) is updated **only on
  stable cutover** (`publish_as_stable` / default `build.ps1` run without
  `-VersionedOnly`/`-Dev`) — it must never point at a build that is not actually in
  the stable namespace.
- CI builds additionally attest build provenance for the release ZIP via
  `actions/attest-build-provenance` (Sigstore keyless, bound to the workflow OIDC
  identity). The digest covers the identical bytes uploaded to blob, so one
  attestation covers all copies. Verify:
  `gh attestation verify <zip> --repo okieselbach/AutopilotMonitor`. Local builds
  cannot produce this attestation — provenance is a CI-only property.

# Citations

- `.github/workflows/build-agent.yml` — "Reserve build number (counter blob CAS)" step
- `scripts/Deployment/build.ps1` (gitignored, contains SAS) — `Request-BuildNumber`
- `src/Agent/AutopilotMonitor.Agent.V2/AutopilotMonitor.Agent.V2.csproj` —
  `BUILD_NUMBER` property + `PersistBuildCounter` target
