# Log

## 2026-07-20

* **Update**: Reworked `agent/mdm-reboot-coalescing.md` after the first real enrollment (session b2e890c1): 2800 records are now **aggregated per burst** into ONE neutral Info event (`rebootUris[]`/`uriCount`/`firstRebootUri`; watermark persisted at flush so a pre-emit reboot kill loses nothing); the raw event no longer claims a reboot — ANALYZE-ESP-005 is gated on an actually-observed `system_reboot_detected`. **Removed** the Shell-Core `esp_reboot_coalescing` event entirely: the "RebootCoalescing" token appears in the routine `SubcategoryProcessing_Started` bootstrap marker on every enrollment (false positive by construction). Verified 2800 description shape recorded.
* **Creation**: Added `agent/mdm-reboot-coalescing.md` — the agent now attributes the "unexpected mid-ESP reboot + second sign-in" pattern (PMPC research) to the device-assigned policy URIs that forced it: new `MdmRebootPolicyTracker` (DM-Enterprise EventID 2800, per-URI `mdm_policy_reboot_required` events, cross-restart RecordId watermark), a `RebootCoalescing` branch in `ShellCoreTracker` (`esp_reboot_coalescing`, backfill-emitting by design), and the `ANALYZE-ESP-005` advisory (reassign device → user groups). ConfigVersion 35; event types deliberately distinct from `system_reboot_detected` so RebootCount stays uninflated.

* **Creation**: Added `backend/business-timestamps.md` (new `backend/` section) — the 2026-07-18 migration reset every row's system Timestamp, exposing that AuditLogs/OpsEvents/Events had (unknowingly) displayed and filtered on it all along; documents the `OccurredUtc` column, the RowKey decoders and RowKey-range date/retention clauses in `BusinessTimestamp`, the GUID-guard trap, the audit/ops backfill endpoint, and the "system Timestamp is never authoritative" migration rule.
* **Creation**: Added `rules/gather-rule-phase-scoping.md` (new `rules/` section) — gather rules gain `activePhases` (run only during listed phases), `activeFromPhase` (sticky latch from a phase onwards, never via Failed) and `emitMode: "on_change"` (canonical-hash result dedup with `suppressedPolls` observability), ConfigVersion 34. Includes the prerequisite backend fix: the custom-rule toggle PUT no longer wipes the stored rule definition (partial-merge in `GatherRuleService.UpdateRuleAsync`).

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
