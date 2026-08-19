---
type: Concept
title: Customer Script Publishing — download.autopilotmonitor.com/agent/
description: How the customer-facing PowerShell scripts reach the download alias, why publishing is decoupled from agent releases, and the guards that keep the published copy, the version badge and the portal's version oracle in agreement.
resource: /.github/scripts/Publish-BootstrapScripts.ps1
tags:
  - build
  - ci
  - release
  - bootstrap
  - download-alias
timestamp: 2026-08-19T00:00:00+02:00
---

# Overview

Four PowerShell scripts are served to customers from `https://download.autopilotmonitor.com/agent/`:

| Blob | Source | Consumed by |
| --- | --- | --- |
| `Install-AutopilotMonitor.ps1` | `scripts/Bootstrap/Install-AutopilotMonitor.ps1` | Intune platform script (customer uploads a copy); downloaded live on every device by the WDP bootstrap MSI |
| `Install-AutopilotMonitor-Dev.ps1` | rendered from the same source | dev fleet |
| `Test-ShouldBootstrapAgent.ps1` | `scripts/Bootstrap/Test-ShouldBootstrapAgent.ps1` | customer dry run via `irm ... \| iex` |
| `Grant-AutopilotMonitorAddOn.ps1` | `scripts/CustomerSetup/Grant-AutopilotMonitorAddOn.ps1` | optional Graph permission grant |

`Uninstall-AutopilotMonitor.ps1` is deliberately not published — nothing links it, and an
unauthenticated uninstall script on the public download host is not a feature.

The alias is a Front Door route in front of the `agent` container; see
[url-registry.md](url-registry.md) for the host registry and the migration to the alias.

# Publishing chain

`.github/scripts/Publish-BootstrapScripts.ps1` is the single owner. Two workflows call it
with identical arguments:

* **`publish-scripts.yml`** — on every push to `main` touching `scripts/Bootstrap/**`,
  `scripts/CustomerSetup/**` or the publisher itself, plus `workflow_dispatch` with a
  `dry_run` input.
* **`build-agent.yml`** — inside the `publish_as_stable` branch, after `version.json` is
  uploaded, so a release lands a set consistent with what is already live.

Before this split, the script blobs rotated **only** on a stable agent release. A bootstrap
fix therefore sat unpublished until the next agent cutover — while the WDP MSI downloads the
published copy on every enrolling device. Script publishing is now decoupled from the agent
release cadence; the release path still publishes, so it can never fall behind either.

Both workflows share the concurrency group `agent-blob-publish` (`cancel-in-progress: false`).
Both write `version.json`, and the publisher's `bootstrapVersion` read-modify-write must not
interleave with a release writing the agent fields.

# Publisher steps

1. **Parse** `$ScriptVersion` from the bootstrap source.
2. **Dev render** — literal substitution of the two `$AgentDownloadUrl` / `$VersionJsonName`
   defaults. A missing anchor literal is a hard failure: a silently un-substituted dev script
   would point the dev fleet at the stable agent.
3. **Version-bump guard** — if the published bootstrap differs from the source but carries the
   same `$ScriptVersion`, the run aborts. See [Version oracles](#version-oracles).
4. **Upload** every blob with `Cache-Control: no-cache` (they all rotate in place), mirrored
   fail-soft to the legacy storage account.
5. **Reconcile the version oracles.**
6. **Verify through the alias** — re-download each blob and compare SHA-256, six attempts
   20 s apart. A persistent mismatch means blobs were written but Front Door serves stale
   content, and the error names the `az afd endpoint purge` command.

## Deterministic publish form

Every blob is published as **CRLF, UTF-8 without BOM**, normalized by the publisher rather
than taken from the working copy. Git normalizes line endings on checkout — a Windows runner
yields CRLF, a `core.autocrlf=false` clone yields LF — so publishing raw working-copy bytes
would emit different blobs for identical source and make every byte comparison, guard and
verification alike, depend on where the workflow ran. CRLF is also what PS 5.1 expects on the
device and what has always been published.

The ASCII-only rule for `scripts/Bootstrap/*.ps1` (enforced by the gates below) is unrelated
but adjacent: PS 5.1 reads BOM-less files as ANSI and corrupts multi-byte characters.

## Version oracles

Three places state the bootstrap script version, and all three are derived from
`$ScriptVersion` in the source:

* `version.json.bootstrapVersion` — read by the docs badge. Written by the publisher as a
  read-modify-write under `If-Match`, so a concurrent agent release keeps its own fields.
  `build-agent.yml` seeds the field when it builds the manifest so the published manifest is
  never briefly missing it; the publisher owns and re-asserts it afterwards.
* `AdminConfiguration.LatestBootstrapV2ScriptVersion` — read by the portal to tell a customer
  their uploaded copy is outdated. **Only** the publisher writes it; `build-agent.yml` writes
  the `LatestAgentV2*` fields and nothing else. One writer per field.
* The `Bootstrap script version: v<x>` marker the script logs, parsed out of captured platform
  script stdout by `utils/bootstrapVersion.ts`.

The version-bump guard exists because those three would otherwise keep asserting a version
that no longer matches what customers download: changed content under an unchanged version is
invisible to every consumer.

# Gates

`.github/workflows/bootstrap-script-gates.yml` is a `workflow_call` workflow holding the
checks once:

* Pester suite under **Windows PowerShell 5.1** — the runtime IME uses for platform scripts.
* ASCII-only scan over `scripts/Bootstrap/*.ps1`.
* `Publish-BootstrapScripts.ps1 -DryRun` — exercises the dev-render anchors and the
  version-bump guard, and reports per blob whether the alias is current, stale or missing.

Two callers: `pester.yml` on pull requests (reports, does not block the merge button), and
`publish-scripts.yml` on pushes to `main`, where the gates job is a `needs:` dependency of the
publish job and therefore **blocks** the upload. `pester.yml` deliberately has no push trigger
— it would duplicate the gate run with no consequence.

# Operating it

* Dry run and drift check, no credentials needed for the read paths:
  `pwsh .github/scripts/Publish-BootstrapScripts.ps1 -DryRun`
* Republish without a code change: `workflow_dispatch` on **Publish customer scripts** with
  `dry_run=false`.
* Front Door serving stale content after a successful upload:
  `az afd endpoint purge --resource-group rg-autopilotmonitor-prd-gwc --profile-name autopilotmonitor-fd --endpoint-name apm-download --content-paths '/agent/*'`

# Citations

* `.github/scripts/Publish-BootstrapScripts.ps1`
* `.github/workflows/publish-scripts.yml`, `.github/workflows/bootstrap-script-gates.yml`, `.github/workflows/pester.yml`
* `.github/workflows/build-agent.yml` (the `publish_as_stable` branch of the upload step)
* `src/Agent/AutopilotMonitor.BootstrapMsi/Invoke-BootstrapDownload.ps1`
* `src/Web/autopilot-monitor-web/utils/bootstrapVersion.ts`
