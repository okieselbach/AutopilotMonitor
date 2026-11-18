# 05. Validation, Intune integration and lab test plan

Event names below are the real ones from `Constants.cs` (`EventTypes`). Where I describe what you will *see* in the portal, that comes from the docs; I have not seen a running instance.

## Part 1. Validation checklist (after deployment)

Run `scripts/07-Test-Deployment.ps1` first. It does the anonymous checks. The rest need a real device.

### Health checks

| # | Check | How | Expected | If not |
|---|---|---|---|---|
| H1 | API alive | `Invoke-RestMethod "$api/api/health"` | JSON with `version`, `commitHash`, `buildUtc`. 30 requests/min/IP limit: do not loop. | 5xx: see "Key Vault reference" below. Empty `commitHash` means a local build, not CI. |
| H2 | Auth gate | `GET $api/api/metrics/summary` anonymously | **401** | 200 means the API is open. Stop and investigate. |
| H3 | Key Vault references resolved | `az rest --method get --url ".../sites/<func>/config/configreferences/appsettings?api-version=2023-12-01" --query "value[].{n:name,s:properties.status}"` | every row `Resolved` | `Unresolved`: secret missing, or MI lacks *Key Vault Secrets User*, or role not yet propagated (wait 5 min, restart the app). |
| H4 | Tables created | `az storage table list --account-name $stg --auth-mode login -o table` | about 40 tables incl. `GlobalAdmins`, `TenantConfiguration`, `Sessions`, `Events*` | Empty: app has not started, or MI lacks Table Data Contributor. Check `AzureStorageAccountName`. |
| H5 | Queues exist after first activity | `az storage queue list ... --auth-mode login` | queues appear (`critical-table-backup-jobs`, etc.) | Missing: `DataStorage__queueServiceUri` wrong or missing (G9). |
| H6 | Dashboard | Open `$portal` | Sign-in page; after login, dashboard. `$portal/version.json` shows the commit. | Redirect loop or `AADSTS50011`: redirect URI mismatch. `AADSTS700016`: wrong client id baked in the build. Placeholder `YOUR_CLIENT_ID_HERE` in HTML: rebuild with `NEXT_PUBLIC_ENTRA_CLIENT_ID`. |
| H7 | Live updates | Dashboard shows a "connected" state; no CSP errors in browser console | SignalR negotiates | CSP `connect-src` missing your hosts (re-run patcher). SignalR Free quota hit. |
| H8 | Agent artifacts | `07-Test-Deployment.ps1` | `version.json`, ZIP, scripts all HTTP 200 anonymously | 404: publish workflows not run. 403: container public access off. |
| H9 | Signature | `Get-AuthenticodeSignature Start-AutopilotMonitor.ps1` | `Valid`, subject contains your organisation | `UnknownError`/`NotTrusted` on a device: certificate not trusted there (G3). |
| H10 | mTLS passthrough (**the one that bites**) | From a *real Intune-enrolled device*, run the loader manually (see T1) | Session registers | `client_certificate_required`/403: client-cert mode wrong or TLS inspection in the path. |
| H11 | MCP | `07-Test-Deployment.ps1 -McpUrl` | `/health` 200 with docs chunk count; OAuth metadata `resource` equals your MCP URL; `/mcp` anonymous = 401 | Metadata resource differs: `MCP_PUBLIC_URL` not pinned. 5xx on first call: cold start, retry once. |
| H12 | Cost guard | Cost analysis for the RG; Log Analytics *Usage* | Under budget; ingestion under the cap | Over: lower sampling or cap. |

### Telemetry checks (needs a real enrollment; see test T1)

| # | Check | Expected in portal | Expected event types |
|---|---|---|---|
| E1 | Enrollment telemetry | Session appears **within seconds** of the agent starting, with device model, serial, OS build, validation method shown (**Autopilot Device Validation**, or **Intune Enrollment** / **Device Association** for Device Prep) | `agent_started`, `agent_version_check`, `log_entry`, `performance_snapshot`, `network_state_change` |
| E2 | ESP monitoring | Timeline shows ESP phases (Device setup, Account setup) with durations; current phase while the device is still at ESP | `phase_transition`, `esp_state_change`, `esp_ui_state`, `esp_provisioning_status` |
| E3 | Win32 app monitoring | Each app with download, install, detection result, exit code, duration | `app_download_started`, `download_progress`, `app_install_started`, `app_install_completed` / `app_install_failed`, `app_post_install_detection`, `app_install_skipped` |
| E4 | Scripts | Platform scripts listed with outcome | `script_started`, `script_completed` / `script_failed` / `script_timeout_suspected` |
| E5 | Completion | Status **Succeeded**, agent removes itself | `enrollment_complete` (or `whiteglove_complete`), `desktop_arrived`, `agent_shutting_down` |
| E6 | Failure | Status **Failed**, rule findings with remediation | `enrollment_failed`, `esp_failure_advisory`, `error_detected`, `esp_apps_failure_correlation` |
| E7 | Backfill | Events from before the agent existed are present | `historic_ime_replay_detected` |

### Troubleshooting: the five things that go wrong first
1. **No session, nothing in the portal.** In order: validation method not enabled (all uploads rejected, the setup guide lists turned-away devices); manufacturer not on whitelist; device not Autopilot-registered in *this* tenant; agent never installed (guards); client cert not passed (G6). On the device read `C:\ProgramData\AutopilotMonitor\Logs\` (`bootstrap-loader.log`, `bootstrap-script.log`, agent log).
2. **Loader says signature invalid.** Certificate chain not trusted on the device, or the **publisher pattern** in the loader does not match your certificate subject. Compare `$ExpectedPublisher` with the `O=` in your certificate.
3. **Agent installs but never uploads.** Check for `telemetry_upload_blocked` in the agent log (`client_certificate_required`, `foreign_response`): TLS inspection or wrong client-cert mode.
4. **Portal loads, API calls fail with CORS errors.** Function app CORS does not list the portal origin (Bicep sets it to the SWA host). A custom domain needs adding.
5. **401 on everything after sign-in.** Token audience mismatch: app registration `api://<id>` vs `EntraId__ClientId` app setting; or access token version 1 (check `requestedAccessTokenVersion = 2`).

---

## Part 2. Intune integration

### Bootstrap script deployment
Upstream's supported method: **platform script** containing the small signed **loader** (`Start-AutopilotMonitor.ps1`). The loader fetches and verifies the real installer on each device, so the copy in Intune does not need re-uploading when the installer changes.

**Option 1 (scripted):** `scripts/06-Deploy-IntuneBootstrap.ps1` (verifies the signature before uploading).
**Option 2 (portal):** *Intune admin center > Devices > Scripts and remediations > Platform scripts > Add > Windows 10 and later*.

| Setting | Value |
|---|---|
| Name | `Start Autopilot Monitor` |
| Script | your signed `Start-AutopilotMonitor.ps1` from `…/agent/` |
| Run this script using the logged on credentials | **No** (SYSTEM). `Yes` makes it report an error and do nothing. |
| Enforce script signature check | **No** (devices do not have your cert in Trusted Publishers; the loader verifies by itself) |
| Run script in 64-bit PowerShell host | **Yes** |
| Assignments | Pilot: `Autopilot-Pilot`. Then: `AutopilotMonitor-Devices`. |

**Assignment strategy**
1. **Pilot** on `Autopilot-Pilot` (devices with group tag `AM-Pilot`) for the first 5 to 10 enrollments.
2. Widen to `AutopilotMonitor-Devices` (all Autopilot-registered devices) only after T1 to T5 pass. The loader's guards (no user profile, no previous logon, uptime under 12 h, no previous deployment marker, no agent present) make this safe on existing devices. Upstream's own words: safe to assign to *All devices*, but the dynamic Autopilot group avoids touching kiosks and Teams Rooms.
3. **Device Preparation:** do *not* rely on the script. Deploy the **MSI** (below).
4. **Rollback:** unassign the script. Devices already carrying the agent finish and remove themselves. Nothing persistent is left except a registry marker `HKLM:\SOFTWARE\AutopilotMonitor\Deployed`.

**Detection methods** (platform scripts have no detection rule; use these):
- *Intune:* script run status per device (Success/Failed) under the script's **Device status**. That only says the loader *ran*, not that monitoring started.
- *Functional (the one that matters):* a **session appears in the portal** for that serial within minutes. Make this your acceptance test.
- *On device:* scheduled task `AutopilotMonitor-Agent` exists during enrollment; folder `C:\ProgramData\AutopilotMonitor\Agent` while running; registry marker after completion.
- *Dry run on any machine, read-only:* `irm '<agent container>/Test-ShouldBootstrapAgent.ps1' | iex` prints each guard's result and the install decision.

### MSI line-of-business app (Device Preparation)
1. Download `AutopilotMonitor-Bootstrap.msi` from your `agent` container (built and signed by `build-msi.yml`).
2. *Intune > Apps > Windows > Add > Line-of-business app*, upload the MSI.
3. Assign **Required** to the **device group selected inside your Device Preparation policy** (the group devices join at enrollment). Any other group means it installs too late. This is the most common mistake (upstream docs).

### Pilot group design

| Group | Type | Membership | Used for |
|---|---|---|---|
| `AutopilotMonitor-Devices` | Dynamic **device** | `(device.devicePhysicalIds -any _ -startsWith "[ZTDId]")` (upstream's documented rule) | Target of the platform script. Broad on purpose; the script is guarded. |
| `Autopilot-Pilot` | Dynamic **device** | `(device.devicePhysicalIds -any _ -eq "[OrderID]:AM-Pilot")` (group tag `AM-Pilot` set at Autopilot import) | Target of your pilot Autopilot profile, ESP policy and apps; first script assignment. |
| `AutopilotMonitor-Admins` | Assigned **user** security group | You and anyone who may open the portal/MCP | Enterprise-app assignment (*Assignment required = Yes*), optional mapping to app role `Admin`, CA policy target. |
| *(per Device Prep policy)* | Assigned device group | Chosen in the Device Preparation policy | Target of the MSI. Not created by my scripts because Device Prep requires specific ownership setup in Intune. |

Dynamic groups need Entra ID P1 and take minutes to populate; do not test straight after creating them.

---

## Part 3. Lab test plan

**Recommended rig:** Hyper-V **Generation 2** VMs with vTPM and Secure Boot on, Windows 11 Enterprise or Pro ISO. Hyper-V reports manufacturer **"Microsoft Corporation"**, which is already on the hardware whitelist. Capture the hardware hash with `Get-WindowsAutopilotInfo -GroupTag AM-Pilot -Online` (from the VM, at OOBE press Shift+F10) or CSV import. Snapshot the VM at OOBE so each test starts clean (note: the agent leaves a registry marker, so a **reset VM from snapshot** is what gives a fresh run, not "Reset this PC").
**Certificate trust (self-signed route):** every test VM must trust `lab-signing.cer` in `LocalMachine\Root` before Autopilot starts (doc 04 step 8, item 5). Take the checkpoint *after* adding it. Keep one VM **without** the trust for the negative test in T9.
Every test: record start time, serial, expected result, actual result, session ID.

| ID | Scenario | Setup | Steps | Expected result and telemetry |
|---|---|---|---|---|
| **T0** | **Client-cert mode decision** (do first) | Bicep `clientCertMode=Optional` | 1) Open the portal from an **Intune-enrolled admin PC**: does the browser prompt for a certificate? 2) Run T1. 3) Repeat 1 and 2 with `OptionalInteractiveUser`. | Pick the mode where: portal never prompts **and** the agent uploads succeed (look for `telemetry_upload_blocked` and HTTP 403 on larger batches). Record the choice in your runbook. |
| **T1** | **New Autopilot deployment (user-driven, Entra join)** | VM tagged `AM-Pilot`, in `Autopilot-Pilot`; profile + ESP assigned; script assigned to pilot; validation on | Boot VM, sign in at OOBE, let ESP run | Session within seconds: E1. ESP phases: E2. Apps: E3. Scripts: E4. End: `enrollment_complete`, `desktop_arrived`, `agent_shutting_down`; status **Succeeded**; agent gone, marker set. |
| **T2** | **Existing device enrollment** (negative test: must NOT install) | An already-enrolled, in-use VM; script assigned via `AutopilotMonitor-Devices` | Wait for the next IME sync or run Sync; run `Test-ShouldBootstrapAgent.ps1` | Dry-run output shows a failed guard (real user profile / previous logon). **No session** in the portal. No agent folder. This proves the broad assignment is safe. |
| **T3** | **Win32 app failure simulation** | Win32 app packaged with a script that **exits 1618 or 1603**, or a wrong detection rule, set as ESP-blocking | Run T1 flow | `app_install_started` → `app_install_failed` (exit code shown); with a bad detection rule: `app_post_install_detection` negative. ESP shows the app as the blocker; rule findings and remediation text appear; `esp_apps_failure_correlation` / `esp_failure_advisory` if ESP fails. Use MCP: *"why did this app fail"* (doc 06). |
| **T4** | **Slow download** (Win32 telemetry) | Large app, throttle VM NIC (Hyper-V bandwidth management) | T1 flow | `app_download_started`, `download_progress` series; Delivery Optimization rollup in app metrics. |
| **T5** | **ESP timeout simulation** | ESP timeout set to **5 minutes** in the ESP policy; app that sleeps longer (a PowerShell Win32 app with `Start-Sleep 600`) | T1 flow | ESP hits timeout; `esp_state_change` / `esp_failure_advisory`; session status **Failed** (or timeout path); blocking app identified in the timeline; timeline gap visible. Restore the timeout afterwards. |
| **T6** | **Autopilot Device Preparation** | Device Prep policy (user-driven), device group with **Intune Provisioning Client** as owner, MSI assigned **Required** to that group; validation: **Intune Enrollment** (grant `IntuneDeviceBinding` add-on) or **Corporate Identifier** | Enroll a VM via Device Prep | Session flagged as Device Prep with its own phase model (3 fixed phases: policies + LOB + M365 Apps; scripts; Win32/Store/Catalog apps). Agent present from phase 1 **only because of the MSI**. Repeat with the platform script alone: agent starts late and misses LOB and M365 install (documented limit). Validation method shown in the session details. |
| **T7** | **Hybrid Join** | AD DS DC, Entra Connect syncing the OU, **Intune Connector for AD** on a server, hybrid Autopilot profile, VM can reach the DC (VPN or same vSwitch) | T1 flow with a Hybrid profile | Account Setup waits for device registration; known patterns flagged by built-in rules (*Hybrid join late device registration*, *stuck in Account Setup*). Watch the agent's `--tenant-id-wait` behaviour (default 600 s). If the device registers late: analysis finding rather than a silent gap. |
| **T8** | **White Glove / pre-provisioning** | pre-provisioning profile | Technician phase then user phase | `whiteglove_complete` for the technician phase; second session or continuation for user phase. |
| **T9** | **Negative security tests** | n/a | (a) Run the agent from a **non-registered** VM; (b) a VM with an **unlisted manufacturer** (e.g. VMware); (c) call the API with **no cert**; (d) sign in with a **guest** account; (e) run the loader on a VM that does **not** trust the lab certificate | (a)(b) rejected, listed in the setup guide as turned away with the reason; (c) 401/403; (d) no tenant claim, no role; (e) loader refuses: signature not Valid, nothing installed. |
| **T10** | **Portal and MCP RBAC** | Viewer account vs Admin | Open session details, diagnostics, settings | Viewer cannot change settings or request diagnostics; MCP shows only what the role allows. |
| **T11** | **Rotation and recovery** | n/a | Run runbook items R3 (secret rotation) and R6 (restore from soft-delete) | Service recovers; no agent impact. |

### What "good" looks like for the evaluation (acceptance criteria)
- T1, T3, T5, T6 produce correct, readable timelines with the expected event types.
- T2 and T9 show the guards and validation **refuse** what they should.
- A failed session is diagnosable from the portal **without** touching the VM, and MCP gives the same answer faster (doc 06).
- Monthly cost stays under the budget you set.

### Rig cost tip
Run the lab VMs on a local Hyper-V host (free). Azure VMs for the AD domain controller cost more than the whole monitoring platform; build the DC locally and use a Hyper-V internal switch.
