# Log

## 2026-07-18

* **Creation**: Added `agent/endpoint-migration.md` — config-channel agent re-home (`MigrateToApiBaseUrl` on `GET agent/config`, ConfigVersion 33): backend or per-tenant API-URL moves without DNS indirection (impossible due to mTLS/Flex-TLS). Live-fetch-only with cache strip, `AgentEndpointMigrationRules` allowlist validated on both sides, one hop, kill-switch wins. Built as the lesson from the 2026-07-18 WEU→GWC cutover, which hard-cut all in-flight agents.

## 2026-07-17

* **Creation**: Added `agent/build-counter-blob.md` — agent build numbers now come from a shared public counter blob (`buildcounter-v{N}.txt`, reserve-before-build with ETag-CAS) so local `build.ps1` and CI `build-agent.yml` builds can never mint the same number. Documents the CI parity fixes (SummaryDialog.exe.config in the ZIP, EXE-vs-manifest version check, hash-oracle update gated on stable cutover) and the Sigstore provenance attestation on CI-built ZIPs.

## 2026-07-16

* **Creation**: Added `agent/autopilot-ztd-diagnostics.md` — reference for Windows' own ZTD diagnostic surfaces (ModernDeployment Autopilot event IDs incl. 807/809/815/908, the `Diagnostics\Autopilot` registry key, deployment-service endpoints, known-issue error-code map with KB fixes). Backs the agent's `ZtdEvidence` collector (`ztdVerdict` on `autopilot_profile_missing`) and the backend known-issue rules; sources carry a re-check RSS feed.

## 2026-07-13

* **Removal**: Moved `customer-graph-add-on-permissions.md` out of this contributor bundle into the customer documentation repository (`reference/optional-graph-permissions.md`). It was customer-facing product content and did not belong in the tech-docs bundle; removed the entry from `index.md`.

* **Creation**: Added `agent/hello-wizard-unskip.md` — session 772fe502 fix: the `HelloWizardStarted` decision signal (prevention veto + un-skip cure for the policy-disabled Hello shortcut) and the HelloTracker confirmation second read against flip-flopping WHfB CSP values.

## 2026-07-11

* **Creation**: Added the `agent/` section — three concept documents on the V2 agent: `agent/overview.md` (runtime, collectors, telemetry pipeline), `agent/decision-engine.md` (DecisionCore reducer, signals, A/B/C completion arms, invariants), `agent/logs-and-persistence.md` (persisted artifacts, signal log vs spool, recovery/replay flow) plus `agent/index.md`.

* **Creation**: Converted `docs/` into an OKF v0.1 knowledge bundle — added YAML frontmatter to `architecture.md`, `mcp-oauth-flow.md`, and `customer-graph-add-on-permissions.md`; created `index.md` and this log.
