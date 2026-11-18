# Autopilot Monitor: self-hosted lab pack for clefsoft.co.uk

A delivery pack for deploying and evaluating [okieselbach/AutopilotMonitor](https://github.com/okieselbach/AutopilotMonitor) in your own Azure subscription and Microsoft 365 test tenant.

**Read `docs/01` first.** The upstream project is a hosted service, not a self-host product. Its infrastructure code is in a private repo, and several values are compiled in. This pack is a reconstruction, built from the source code, and it says plainly what is verified and what is not.

## What is in here

| Path | What | Verified how |
|---|---|---|
| `docs/01-executive-summary-and-gaps.md` | Summary, decision, **gap register G1 to G18**, delivery plan, open questions, verified/unverified table | n/a |
| `docs/02-architecture-and-prerequisites.md` | Architecture diagram, Azure services, exact Entra app settings, Intune needs, RBAC, licensing, prerequisites checklist | read from source |
| `docs/03-cost-and-security.md` | Cost for lab / small / medium, security review with ranked findings, Conditional Access | cost = my estimate |
| `docs/04-deployment-guide.md` | Naming standard, **step-by-step walkthrough (steps 0 to 15)**, manual option, azd, permissions summary | commands not run against your tenant |
| `docs/05-validation-intune-and-testing.md` | Health checks, Intune script/MSI, pilot groups, **test plan T0 to T11** with expected event types | event names from `Constants.cs` |
| `docs/06-mcp-runbook-and-upgrade.md` | Self-hosted MCP design, Claude Desktop / VS Code setup, prompts, **runbook R1 to R18**, upgrade strategy | MCP env vars read from source |
| `bicep/` | `main.bicep` + 7 modules + `main.bicepparam` | **Compiled and linted** (Bicep 0.49.2), not deployed |
| `azure.yaml` | azd manifest (provision only) | YAML only |
| `scripts/01-set-selfhost-endpoints.mjs` | Patches a fork to your hosts and signing identity | **Run on a real clone** |
| `scripts/03-seed-platform.mjs` | Seeds first Global Admin, identity binding, auto-approve | **Run against Azurite** |
| `scripts/02, 04, 05, 06, 07 *.ps1` | Entra app, secrets, Intune groups, Intune script, health checks | **Not executed** (no PowerShell in the authoring sandbox). Use `-WhatIf`. |
| `scripts/08-New-LabSigningCert.ps1`, `09-Trust-LabCert.ps1` | Self-signed lab signing cert into Key Vault; trust it on test VMs | **Not executed** (Windows-only cmdlets) |
| `cloudflare/download-worker.mjs` + `wrangler.jsonc` | Cloudflare Worker serving the agent download host | **Unit-tested with a stubbed fetch**; not deployed |
| `ci/*.yml` | GitHub Actions: infra, backend, web | YAML syntax only |

## Order of work

1. Landing zone (`bicep`, pass 1). 2. Entra app + secrets. 3. Pass 2 (client id + MCP). 4. Fork + patch + tests. 5. Code-signing. 6. Publish agent/scripts/MSI. 7. Deploy backend + web. 8. Seed + reseed rules. 9. Intune groups/script/MSI. 10. Test plan. 11. Lock down.

Details and every command are in `docs/04`.

## Using it in your fork

Copy this whole folder to `deploy/selfhost/` in your fork. **Do not use `infra/`**: upstream mounts a private git submodule there.

## Not touched

I did not create or change anything under `workflows/` or `tools/` in this repository, and I did not publish anything to Azure, GitHub or your tenant.
