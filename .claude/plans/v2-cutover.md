# V2 Agent Cutover — Stable-Namespace-Switch from V1 to V2

**Date:** 2026-05-09 (Saturday — chosen for low in-flight enrollment volume)
**Goal:** Make V2 the production-stable agent line. V1 frozen but available as one-command rollback via versioned namespace. Build the architecture so V2→V3 later is the same one-command pattern.

---

## Architecture (post-cutover)

### Two namespaces in Azure Blob Storage

**Stable** — what Bootstrap reads. Blob names NEVER change, content rotates per cutover:
```
Install-AutopilotMonitor.ps1, Install-AutopilotMonitor-Dev.ps1
version.json,    version-dev.json
AutopilotMonitor-Agent.zip,    AutopilotMonitor-Agent-dev.zip
```

The Bootstrap script is **generic** — single source `scripts/Bootstrap/Install-AutopilotMonitor.ps1`,
agent-version-agnostic. Build emits two pre-rendered files (prod + dev variant) by literal-string
substitution of the two URL/manifest defaults; Intune cannot pass parameters so the dev channel
needs its own pre-rendered script. The agent owns its own defaults (e.g. 600 s TenantId-wait),
so the script only calls `--install` plain — same Bootstrap works for V1, V2, and any future V3.

**Versioned** — for SelfUpdater-within-line + rollback reserve. One set per major (no Bootstrap here —
the Bootstrap is generic, only Agent + manifest are line-scoped):
```
version-v{N}.json,    version-v{N}-dev.json
AutopilotMonitor-Agent-v{N}.zip,    AutopilotMonitor-Agent-v{N}-dev.zip
```

### Rules
- **Bootstrap is generic and customer-facing.** Single source, no internal-strategy comments.
  Customers' Intune Platform Script is never touched after first deployment.
- **SelfUpdater of line N reads `*-v{N}.*` only.** Never cross-line, never stable. Prevents accidental cross-major upgrades.
- **Build-script of stable line pushes to BOTH** stable + versioned with `-PublishAsStable` flag,
  and re-uploads the Bootstrap (prod + dev variants).
- **Build-script of non-stable line pushes only to versioned agent ZIP + manifest.** Bootstrap untouched.
- **Backend `GetAgentConfig` dispatches per X-Agent-Version-Major** to `LatestAgentV{Major}*` AdminConfig fields. V3 later = add field set + 1 switch-arm.

---

## PR Sequence

### PR1 — Parametric Refactor (no behavior change)

| File | Change |
|------|--------|
| `src/Shared/AutopilotMonitor.Shared/Constants.cs` | Add `AgentVersionFileNameForLine(int)`, `AgentZipFileNameForLine(int)`, `BootstrapScriptNameForLine(int)`. Mark `*V2` constants `[Obsolete]` pointing at the methods. Keep stable-namespace constants (`AgentVersionFileName`, `AgentZipFileName`) — they're the stable namespace, not V1-specific. |
| `src/Backend/AutopilotMonitor.Functions/Functions/Config/GetAgentConfigFunction.cs` | Replace `IsV2Client(version)` with `ParseMajor(version)`. Dispatch via `AdminConfiguration.GetForLine(major)` accessor. |
| `src/Shared/AutopilotMonitor.Shared/Models/Config/AdminConfiguration.cs` | Rename `LatestAgentVersion` → `LatestAgentV1Version` (also Sha256/ExeSha256/BootstrapScriptVersion). `LatestAgentV2*` stays. New `GetForLine(int major)` accessor returns `(version, sha256, exeSha256)` tuple. |
| `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableConfigRepository.cs` | Rename Store/Map calls. **Memory `feedback_table_storage_serialization.md`: bump seed-data version, verify roundtrip.** |
| `src/Web/.../app/admin/components/AdminConfigSettingsSection.tsx` | If V1-fields are surfaced (verify), update bindings. **Memory `feedback_admin_config_ui_roundtrip.md`: any V1-field that is in the AdminConfig PUT roundtrip MUST be updated in 4 web files.** |
| `src/Agent/AutopilotMonitor.Agent/SelfUpdater.cs` (V1) | Switch reads from `Constants.AgentVersionFileName`/`AgentZipFileName` to `Constants.AgentVersionFileNameForLine(1)`/`AgentZipFileNameForLine(1)`. V1 now isolated to its versioned namespace. |
| `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Runtime/SelfUpdater.cs` (V2) | **No change required.** Already reads `version-v2.json` (its versioned namespace). Architecture was correct from start. |
| `src/Backend/AutopilotMonitor.Functions/Services/LatestVersionsService.cs` | `VersionJsonUrl` already points at stable namespace (`version.json`). No change. After PR3, this file shows V2's version automatically. |
| `src/Backend/.../Tests/*` | Update assertions for renamed fields. Run full backend suite. |
| `src/Agent/AutopilotMonitor.Agent.V2.Core.Tests/*` | Verify SelfUpdater tests still pass. |
| `src/Web/.../utils/__tests__/bootstrapValidation.test.ts` | Hardcoded `AutopilotMonitor-Agent.zip` reference is fine — it's the stable namespace. |

**Deliverable:** Refactor green, all 3 test suites pass (Backend xUnit + Agent xUnit + Web vitest). No live behavior change yet.

### PR2 — Build Script Consolidation

| File | Change |
|------|--------|
| `scripts/Bootstrap/v2/Install-AutopilotMonitor.ps1` | New: 1 source file with `@@DOWNLOAD_URL@@` + `@@VERSION_JSON@@` placeholders. ASCII-only (Memory `feedback_ps1_ascii_only.md`). Replaces v2 + v2-dev variants. |
| `scripts/Bootstrap/v1/Install-AutopilotMonitor.ps1` | New: same shape, V1-side. Forked from current `Install-AutopilotMonitor.ps1`. |
| `scripts/Bootstrap/Install-AutopilotMonitor*.ps1` | DELETE — superseded by `v1/` and `v2/` source files. |
| `scripts/Deployment/v2/build.ps1` | New: consolidates V2 release/debug/dev variants. Flags: `-Configuration <Debug\|Release>` (default Release), `-Dev`, `-PublishAsStable`, `-SkipUpload`. Substitutes Bootstrap-Script placeholders before upload. Computes SHA-256, enriches `version-v{N}{-dev,}.json`, updates AdminConfiguration `LatestAgentV{N}*` fields. With `-PublishAsStable`: also uploads to stable namespace + writes additional `LatestAgentV{N}*` field set as the "stable line marker" (no separate primary fields — `GetAgentConfig` discovers stable via blob namespace). |
| `scripts/Deployment/v1/build.ps1` | New: same shape, V1-side. Available as rollback button. |
| `scripts/Deployment/{V2,*}.ps1` (legacy) | DELETE — `scripts/Deployment/build_and_upload_*.ps1` and `scripts/Deployment/V2/*.ps1` superseded. |
| `.github/workflows/build-agent.yml` | Re-point project paths at V2 (`src/Agent/AutopilotMonitor.Agent.V2/...`, test project `AutopilotMonitor.DecisionCore.Tests`, buildcounter at V2 location). Calls `scripts/Deployment/v2/build.ps1 -Configuration Release` (without `-PublishAsStable` initially — workflow does NOT do cutover). |
| `.github/workflows/build-agent-v1.yml` | New (optional): V1 rollback workflow, manual-dispatch only. |

**Deliverable:** PR2 merged but still no live cutover — `-PublishAsStable` not yet executed. Both lines buildable from same flag schema.

### PR3 — Lab Smoke + Cutover (the actual switch)

1. **Pre-flight checks** (see Cutover Checklist below)
2. **Hardware-lab device #1** via current Intune-Platform-Script — verify V1 baseline still works
3. Execute cutover: `cd scripts/Deployment && ./build.ps1` (default = current stable line, pushes to stable + AdminConfig)
4. Wait 60s (blob upload propagation; CDN if any — verify Az CLI shows fresh blob)
5. **Hardware-lab device #2** via same Intune-Platform-Script — must now pull V2
6. Verify in Web-UI: new session shows X-Agent-Version `2.0.x`, hash-oracle returns V2 hashes (check OpsEvents for `IntegrityCheckPassed`/`Failed`)
7. **`/go/{code}` smoke test on lab device #3** — generate code in Web-UI, paste PowerShell, verify V2 install works with `--no-auth --bootstrap-token --tenant-id` flags. **Risk: V2 `Program.InstallMode.cs` may not support all V1 flags identically — verify before counting cutover successful.**
8. (Optional) Rollback drill: `cd scripts/Deployment/v1 && ./build.ps1 -PublishAsStable`, then re-run V2 cutover. Confirms rollback works.

### PR4 — Docs + Memory + Skills

- `docs/architecture.md` — V2 = standard, V1 = rollback-reserve
- `src/Web/.../app/docs/sections/SectionAgent.tsx` + `SectionAgentInternals.tsx` — customer-facing
- `CLAUDE.md` — Agent path note updated to V2
- Memory: bump `project_v2_*` notes, deprecate V1-specific entries, new entry `project_namespace_indirection.md` (stable vs versioned)
- New: `docs/runbooks/cutover.md` — generic V→V+1 runbook, references this plan

---

## Pre-flight Cutover Checklist (PR3)

- [ ] No active enrollments in production (MCP query: `query_table Sessions` filter `LastEventAt > now-30min`)
- [ ] V2-Build green on Saturday's commit (`scripts/Deployment/v2/build.ps1 -SkipUpload` smoke)
- [ ] AdminConfiguration `LatestAgentV2*` fields populated (Az CLI: `az storage entity show --table-name AdminConfiguration --partition-key GlobalConfig --row-key config`)
- [ ] Lab device 1: V1 baseline still works (sanity)
- [ ] Run `-PublishAsStable`
- [ ] Lab device 2: V2 pulls (verify Web-UI session)
- [ ] `/go/{code}` flow works on lab device 3
- [ ] No `IntegrityCheckFailed` events in OpsEvents (15min observation)
- [ ] `/api/health` shows V2 binaries reachable
- [ ] Memory + docs updated

---

## Rollback Runbook

Single-command:
```powershell
cd scripts/Deployment
./build.ps1 -Line 1
```

Wait 60s for blob propagation. Verify:
```powershell
irm https://autopilotmonitor.blob.core.windows.net/agent/version.json
# Must show 1.0.x
```

Done. Bootstrap-Script in Intune unchanged. No customer action required.

---

## V3 Future-Switch Template (this is what we're building toward)

When V3 ships:

1. Add `3 = @{ ProjectPath=...; TestProject=...; BuildOutDir=... }` to `$LineConfig` in `scripts/Deployment/build.ps1`
2. Add `LatestAgentV3{Version,Sha256,ExeSha256,BootstrapScriptVersion}` field set on `AdminConfiguration`
3. Add `case 3:` switch-arm to `AdminConfiguration.GetAgentLine(int)`
4. Lab-test: `./build.ps1 -Line 3 -VersionedOnly` (versioned namespace only, customers don't see it)
5. Cutover-day: `./build.ps1 -Line 3` (default = push to stable) AND bump `$DefaultLine = 3` in `build.ps1`
6. Done. Same Bootstrap-Script in Intune. V2 frozen as rollback reserve (`./build.ps1 -Line 2`).

The architecture from PR1+PR2 is what makes step 6 a one-liner.

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| V2 `Program.InstallMode.cs` doesn't support all V1 install flags (`--no-auth`, `--bootstrap-token`, `--tenant-id`) | Verify in PR1, test in PR3 step 7. If gap → port flags to V2 before PR3. |
| `-PublishAsStable` race condition (build done, blob not yet propagated, lab device pulls partial state) | 60s wait + Az CLI explicit verification before lab device 2 boots. |
| In-flight V1 enrollment during cutover gets confused | Saturday timing + 30min pre-check. Memory note: V1 SelfUpdater is now isolated to v1 namespace, so even if one is mid-poll it won't see V2. |
| AdminConfig field rename breaks existing entries | TableConfigRepository test must verify Store→Map roundtrip on real Azurite emulator before merge. |
| Bootstrap-Script substitution-engine generates malformed script (placeholder leak) | Build-script verifies output: regex-check for any remaining `@@*@@`, fail-fast. |

---

## Out of Scope (deferred)

- V1 code-path deletion (decided: V1 frozen but kept as rollback reserve)
- MCP/Skill for cutover-automation (decided: build-flag is enough today; revisit if V3+ adds more lines)
- Customer-side Intune-Platform-Script update (intentionally never required by design)