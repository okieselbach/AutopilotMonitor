"use client";

import Link from "next/link";
import { PublicPageHeader } from "../../components/PublicPageHeader";

export default function ChangelogPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicPageHeader title="Changelog" />
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        <div className="bg-white rounded-2xl shadow-sm border border-gray-200 p-8 sm:p-10">

          {/* Intro */}
          <div className="mb-10 pb-8 border-b border-gray-100">
            <p className="text-gray-600 leading-relaxed">
              This changelog tracks significant platform changes during Private Preview —
              architecture updates, data flow changes, and anything else that might briefly
              affect the UI or monitoring data. If something looks off, check here first.
              A recent entry might explain it.
            </p>
            <p className="mt-3 text-gray-600 leading-relaxed">
              Found a bug or want to give feedback?{" "}
              <a
                href="https://github.com/okieselbach/Autopilot-Monitor/issues"
                target="_blank"
                rel="noopener noreferrer"
                className="text-blue-600 hover:text-blue-800 underline font-medium"
              >
                Open a GitHub Issue
              </a>
              {" "}— it helps more than you might think.
            </p>
          </div>

          {/* Entries — newest first */}
          <div className="space-y-10">

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-06-16 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Software hub with self-service vulnerability exposure, faster Fleet Health, and expanded MCP tools
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Software hub with self-service vulnerability exposure</span> — The App Health page is now a tabbed <Link href="/apps" className="text-blue-600 hover:text-blue-800 underline">Software</Link> hub bringing app installs, installed-software inventory, and CVE/KEV vulnerability exposure together in one place. For the first time, tenant admins can see their own fleet's vulnerability exposure (Fleet Exposure) — previously a Global-Admin-only view — with per-device matches, a severity breakdown, and data-source sync-status counters.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Faster Fleet Health on large fleets</span> — Fleet Health now loads from server-aggregated metrics endpoints instead of draining up to 200,000 sessions into the browser, so the dashboard opens far faster on big fleets with identical numbers.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">"Not registered" devices overview</span> — A new view under enrollment validation lists, per serial number, devices rejected with HTTP 403 over the last 14 days because they were not in the tenant's Autopilot or Corporate Identifier registry.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Richer session detail</span> — The Session Info card now shows Reboots, Enrollment Type (Device Preparation / Autopilot, with PreProvisioned and Gather Rules qualifiers), Join Type (Hybrid / Entra), and a "last contact" tooltip. Per-session reboot count is also stored and queryable via MCP.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Office &amp; RealmJoin install rows</span> — Microsoft 365 Apps (Office) and RealmJoin packages now appear as first-class rows in the session Install Progress alongside Intune apps, with live timers, durations, and exit-code badges. The RealmJoin agent version is shown in the session System panel, and the RealmJoin watcher is now a per-tenant portal toggle (default off).</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Expanded MCP tools</span> — New <code>get_software_inventory</code> and <code>get_app_install_metrics</code> tools surface installed-software inventory and per-app install metrics (including Delivery Optimization peer/MCC offload savings); <code>get_audit_logs</code> gains action / performed-by / entity-type / entity-id filters; <code>list_tenants</code> and a fleet vulnerability summary were added; raw session/event query tools now return literal table rows; hybrid event search is genuinely semantic; and the tool surface is role-aware so Global-Admin-only tools are hidden from regular users. Analysis output also resolves rule-template placeholders so explanations never leak raw <code>{"{{...}}"}</code> tokens.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Clearer error reporting</span> — Event and error views now show enriched, human-readable error-code descriptions and clearly distinguish a detection-failure from an install-failure from a "likely stuck" enrollment.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Deep links survive re-authentication</span> — Opening a session, diagnosis, or other in-app link in a new tab now returns you to that page after login instead of dropping you on the dashboard.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Health dashboard MCP card</span> — The Health dashboard adds an MCP Server card with version and wake-aware cold-start handling, alongside the SignalR quota card.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Device auto-block on runaway sessions</span> — Devices that emit an excessive number of session events can now be auto-blocked and their sessions killed, with block/kill-by-session-ID shortcuts and ops-alert integration.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">CPU architecture</span> — Device hardware now reports CPU architecture (<code>x86</code> / <code>x64</code> / <code>ARM</code> / <code>ARM64</code>) as a queryable property.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Security hardening</span> — Added <code>nosniff</code> and stricter <code>x-forwarded-for</code> handling, tightened query-filter validation, enforced tenant-id presence on scoped endpoints, and patched a prototype-pollution advisory in a web dependency.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-05-19 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Safe cascade-delete &amp; tenant offboarding, Intune script display names, full Remediation lifecycle, and Agent V2 robustness
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Safe session cascade-delete with restore window</span> — Session deletion is now a snapshot-based, crash-safe cascade across all related tables (events, audit, app installs, vulnerability data, software inventory, etc.) with a deterministic order, hash-verified manifest, and a 33-day restore window.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Async tenant offboarding</span> — Tenant offboarding from <Link href="/settings" className="text-blue-600 hover:text-blue-800 underline">Settings</Link> is now a queued async cascade that flips the tenant to disabled, waits for the config cache to drain, then safely wipes all 24 tenant-scoped tables with fetch-verify-delete semantics. The UI shows a minute-based countdown and you can close the tab — deletion finishes in the background. Self-service re-onboarding still works after a clean offboard.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Optional Graph add-on — Intune script display names</span> — A new <Link href="/settings/tenant/optional-graph-capabilities" className="text-blue-600 hover:text-blue-800 underline">Optional Graph capabilities</Link> page lets tenant admins grant a tightly-scoped Graph permission to resolve <em>real</em> Intune Platform Script and Remediation display names in session timelines (instead of bare IDs). Opt-in only — no app manifest change for tenants who skip it. The page provides a copy-paste PowerShell command (and a <code>Grant-AutopilotMonitorAddOn.ps1</code> helper) with idempotent grant / verify / revoke flows.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Full Intune Remediation lifecycle</span> — Health scripts are now captured end-to-end: detection → remediation → post-detection are folded into a single Remediation cycle card with a parent header showing the overall outcome (Compliant, Remediated successfully, Non-compliant after remediation, Remediation script failed, …). A live "running" indicator appears the moment a script starts, ahead of the consolidated final result. Phase-aware semantics avoid counting a non-compliant detection that was successfully remediated as a "failed" event, and a two-stage emission closes the timing gap on short Autopilot sessions.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">SLA notification spam fix</span> — SLA throttling moved from an in-memory cooldown to a persistent per-tenant status row with CAS-guarded at-most-once semantics. AppInstall SLA now actually evaluates against the same ISO-week window as the dashboard, and a single <code>sla_resolved</code> notification fires when a breach clears. Configurable cooldown via <Link href="/settings/global" className="text-blue-600 hover:text-blue-800 underline">Global Settings</Link>.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Dashboard stats overhaul</span> — The five dashboard cards (Active Sessions, Success Rate, Avg. Duration, Total Today, Failed Today) are now server-aggregated over the full 7-day window instead of derived from the currently-paginated client list, so they no longer drift as the session table grows. Active Sessions is pinned to In-Progress only; Pending (WhiteGlove waiting for power-on) and Stalled remain visible via list filters. Stats reset cleanly on scope changes.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent V2 robustness</span> — Several correctness and recovery fixes: Classic enrollments with Hello disabled no longer deadlock at the AwaitingHello stage (EspExiting now routes through the Hello-disabled fast-path); AwaitingHello is gated on AccountSetup <em>truly</em> succeeding instead of the page-handoff event; new desktop-detector liveness signals (started / first-poll / no-candidate) help distinguish a dead post-reboot agent from broken DAD wiring; four new <code>agent_shutting_down</code> emit paths (Ctrl+C, process exit, unhandled exception, runtime host exit) close gaps that previously left sessions without a shutdown breadcrumb. Hot-path event dictionaries are pre-sized to skip ~3k Gen0 allocations per session.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Fail-soft runtime handoff &amp; ASR resilience</span> — A Defender ASR rule blocking WMI process creation can no longer strand a freshly-bootstrapped device. The agent's <code>--install</code> now runs a two-tier fallback (WMI Win32_Process → <code>schtasks /Run</code> → next BootTrigger reboot) and treats a runtime-spawn failure as a warning, not a fatal error. The bootstrap script's process probe remains the canonical "did the agent come up" signal.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Intune dual-stack certificate selection</span> — On Intune-managed devices that have both the MDM device certificate and the newer MMP-C certificate installed side-by-side, the agent now exact-matches the MDM issuer (<code>CN=Microsoft Intune MDM Device CA</code>) before presenting it for mTLS, instead of letting the OS pick. Resolves a class of <code>SecureChannelFailure</code> / <code>ChainFailed</code> rejections that surfaced on newer Windows builds.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">"Likely stuck" app-install hedge</span> — When ESP times out on the Apps subcategory, the in-flight app is no longer left silently frozen in the Installing bucket. It is now promoted as a hedged "likely stuck" entry with confidence <em>presumed</em>, so operators see which app the enrollment was waiting on at termination.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Notifications &amp; UX polish</span> — Opt-in "enrollment started" webhook (Teams Legacy / Workflow / Slack) fires on fresh session registration and WhiteGlove Part 2 resume, default off so existing tenants don't get a surprise notification storm. The notification bell is now always visible. Sidebar usage telemetry (full / icons / hidden) is captured as a global App Insights property for understanding portal interaction patterns.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Bugfixes &amp; polish</span> — Various fixes throughout the whole platform</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-05-10 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Agent V2 rollout, MS-Graph pagination, SignalR push notifications, and security hardening
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-orange-500 flex-shrink-0">⚠</span>
                  <span><span className="font-medium text-gray-800">Agent V2 rollout (action recommended)</span> — The agent has been rebuilt on a new internal architecture (Decision Engine, lifecycle-anchor allowlist, Death-Rattle recovery for crashed runs) for more reliable session detection and stricter completion logic. The Intune bootstrapper script (<code>Install-AutopilotMonitor.ps1</code>) was updated and now verifies the runtime process started after launch. Replace the script in your Intune tenant with the latest version from the repository.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Submit Logs page</span> — A new <Link href="/settings/tenant/support" className="text-blue-600 hover:text-blue-800 underline">Submit Logs</Link> page lets you send diagnostic files to the Autopilot Monitor team without an associated session, useful for issues caught outside of an active enrollment.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Real-time notifications via SignalR push</span> — The notification bell now updates purely from SignalR events (one fetch on mount, re-fetch on reconnect) instead of polling every 60 seconds, with audience-aware delivery for tenant admins vs members.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">MS-Graph style pagination</span> — All large list endpoints (sessions, events, audit, ops events, reports) now support opt-in <code>nextLink</code> pagination with HMAC-bound continuation tokens. Dashboard, Fleet Health, and MCP tools have been migrated to the new model.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">MCP server</span> — A new <code>get_resource</code> tool, leaner <code>get_session_summary</code> payloads, and a security &amp; capability audit covering OAuth proxy hardening, access-guard tightening, and pagination of the last three legacy diagnostic tools.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Backend security hardening</span> — Client-certificate chain trust is now pinned to the embedded Intune root certificate, and query-tenant guards moved to centralized middleware.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Web hardening &amp; portal split</span> — Tightened Content Security Policy, hardened <code>/go</code> bootstrap error handling, and a host-based middleware now cleanly separates the public site from the authenticated portal.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Web performance</span> — In-flight request collapser (<code>dedupedAuthFetch</code>) eliminates duplicate parallel fetches, the dashboard load path was refactored</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Delivery Optimization</span> — Download breakdowns now include Microsoft Connected Cache (MCC) and LinkLocal sources, applied symmetrically across all layers (collector, telemetry, dashboard).</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">WhiteGlove improvements</span> — Timeline now splits Part 1 / Part 2 at the Part-2 <code>agent_started</code> event (not the legacy <code>whiteglove_resumed</code>), Part-2 architectural cleanup landed on the agent side, summary dialog is suppressed on Part 1, and Modern Deployment EventID 1010 was added to the harmless-noise default list.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Bugfixes &amp; polish</span> — <code>TenantIdResolver</code> fallbacks for non-Type-6 enrollments and Hybrid Join (CloudDomainJoin + MS-Organization-Access cert), event-driven TenantId wait via RegistryWatcher, software-inventory now accepts AAD/MSA SIDs and emits real registry paths, full-width dashboard toggle with <code>?span=</code> URL persistence, SLA violator link routes correctly, and many smaller fixes across pagination, audit fetching, and submit-diag sidebar entries.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-04-16 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Completion state machine, SLA &amp; App Health dashboards, Ops Alerts, and Device Preparation groundwork
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Session completion state machine</span> — The agent soon uses a dedicated <code>CompletionStateMachine</code> that combines multiple signals (ESP final exit, Hello, Desktop arrival) to decide when an enrollment is truly done. This fixes several cases where WhiteGlove and Hybrid Join sessions were misclassified or never marked complete.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">SLA tracking dashboard</span> — New SLA monitoring page with per-tenant configuration and notification support when SLAs are breached.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">App Health dashboard</span> — New global view of app deployment health with scoped drill-downs and a configurable column picker.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Ops Events &amp; Ops Alerts</span> — Operational event log plus admin alerts for backend health, blob storage, runaway sessions, and excessive event counts per session.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent emergency / distress channel</span> — A separate low-overhead channel so the agent can still report critical errors when the normal telemetry path is impaired.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Enhanced analyze rule engine</span> — New <code>in</code> / <code>not_in</code> compare operators, <code>MarkSessionAsFailed</code> action, template variables, per-rule stats card, and a new ESP certificate-error analyze rule (<code>ANALYZE-ESP-002</code>).</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Delivery Optimization</span> — OS-level DO collector, P2P totals in download progress, and DO usage stats in the geographic drill-down.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Vulnerability matching improvements</span> — Fuzzy (Jaro-Winkler) CPE matching, confidence levels, data freshness indicators, CVE mapping column in the vulnerability report, and WhiteGlove sessions now also get a vulnerability report.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Device Preparation (WDP v2) groundwork</span> — The agent now distinguishes Classic vs v2 Autopilot flow, and a device-association validator was added on the backend. Device Preparation support is still in active validation.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">IME version history</span> — Intune Management Extension version history is tracked and surfaced via MCP; agents running on outdated IME versions trigger an alert.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Known Issues page</span> — Dedicated docs page for ongoing issues (replaces the inline list that used to live in this changelog).</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">MCP server</span> — Stateless endpoint, tools split into domain modules, new ops-events tool, tool-call telemetry, improved semantic + keyword search, and an integration test suite.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Security hardening</span> — Centralized tenant-isolation middleware, OData sanitizer, hardened agent config endpoint, cross-tenant fallback fixes, session-aware auto-unblock, and additional request-size / integrity guards on the self-update path.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Web performance &amp; refactor</span> — Lazy session loading, response compression, more parallel fetches, and a large internal restructuring of the web app into hooks and utils for easier maintenance.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Bugfixes &amp; UX polish</span> — Quick search, bootstrap scripts, webhook notifications, WhiteGlove timeline rendering, phase-timeline regressions, report upload size, summary dialog launch fallback, NTP / timezone defaults, and many more small fixes.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-30 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Updated bootstrapper script, agent crash detection, and quick search
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-orange-500 flex-shrink-0">⚠</span>
                  <span><span className="font-medium text-gray-800">Updated bootstrapper script (action recommended)</span> — The bootstrapper script (<code>Install-AutopilotMonitor.ps1</code>) now uses SHA-256 integrity verification for agent downloads instead of MD5. If you deployed the script via Intune, it is recommended to replace it with the latest version from the repository for improved security.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent crash detection</span> — The agent now detects and reports unexpected crashes with automatic recovery. Platform-level metrics (CPU, memory, disk) are collected alongside enrollment events for better diagnostics.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Global quick search</span> — A fuzzy search across sessions, devices, and users is now available from the navigation bar for fast lookups.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Rate limiting</span> — Per-user request rate limiting protects the backend from excessive API usage.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Bugfixes</span> — Vulnerability report rescan persistence, orphaned session handling, timezone parsing, and NTP clock-skew warnings improved.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-26 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Software inventory &amp; vulnerability analysis, new agent signals, and settings overhaul
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Software Inventory &amp; Vulnerability Analysis</span> — The agent now discovers installed software across Registry, WMI, AppX/MSIX, and per-user sources and correlates it against NVD and CISA KEV databases. The dashboard shows a vulnerability report with CVSS scores and severity levels. Includes 240+ curated CPE mappings and strict AppX whitelist filtering.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">SecureBoot &amp; time sync</span> — The agent collects SecureBoot certificate details (with a new analyze rule), auto-detects the timezone, and checks NTP offset to catch time-related enrollment failures.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Security hardening</span> — Request size limits on all submission endpoints and symlink detection in diagnostic paths guard against DoS and path-traversal attacks.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Settings reorganization</span> — The sidebar now uses expandable sections for a cleaner navigation. Tenant settings were restructured and consolidated.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">OOBE Config viewer</span> — A modal dialog decodes the OOBE configuration bitmask, showing each bit flag with description and confidence level, and detects the enrollment profile type.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">FAQ page</span> — New Docs section covering supported scenarios, deployment, agent capabilities, and troubleshooting.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-19 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Navigation overhaul, session architecture, new agent signals, and community rules
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Unified sidebar</span> — The entire navigation has been redesigned with a global sidebar. The old top nav is gone; settings and admin areas now have their own sidebar sections. Mobile layout also reworked.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Session index table</span> — Session storage has been fundamentally re-architected for better scalability and reliability.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">New agent signals</span> — The agent now reports <code>agent_shutdown</code> (clean shutdown), <code>hardware_spec</code> (hardware inventory at enrollment), network interface changes, and clock skew deviations for better diagnostics.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Self-deploying mode detection</span> — The agent now automatically detects self-deploying scenarios and tracks the enrollment finalization process with dedicated events.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Notification providers</span> — The webhook notification system now supports three providers: Teams Legacy, Teams Workflow, and Slack — selectable per tenant.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Community rules</span> — A community rule set for gather and analyze rules has been added. Rules now have a JSON view, severity override, and centralized guardrails. New local admin analyze rule included.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Geographic drill-down</span> — The geographic performance view now supports drill-down to region and country level.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Mark as success</span> — Sessions can now be manually marked as successful, e.g. after manually resolved enrollments.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Feedback system</span> — An integrated feedback system with admin management allows direct feedback from within the portal.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Tenant settings UX</span> — The central save button in tenant settings has been replaced with individual section save buttons. A new Unrestricted Mode option disables most guardrails per tenant request.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Docs expanded</span> — New general documentation section, IME pattern explanation, and a public sites sidebar added.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Backend reliability</span> — Improved cache invalidation and retry logic for transient errors.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-10 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Security architecture, session timeline improvements, and new agent capabilities
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Role-based access control</span> — Admin and Operator roles with role management in Settings. API authorization and policy enforcement middleware ensure proper access control across all endpoints.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent self-update</span> — Agents can now update themselves automatically, ensuring outdated versions in the field get replaced without manual intervention.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Bootstrap sessions</span> — New bootstrap session flow with explicit token enablement for initial device onboarding. (support for bootstrap tokens enabled by request)</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Raw event timeline</span> — A new raw view of the event timeline with full search support, useful for deep-dive troubleshooting.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Enrollment summary dialog</span> — Optional summary dialog shown at the end of enrollment, with event timeline search and clickable phases in the phase tracker.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Original ESP tracking</span> — The agent now tracks the original ESP provisioning status to catch non-IME errors such as certificate failures.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Analyze &amp; gather rules</span> — Added negative compare operators for analyze rules, XML and JSON gather options, and a built-in &ldquo;old OS version&rdquo; warning rule.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Email notifications</span> — Email notification (Welcome and instructions) for Joining the Private Preview.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent version management</span> — Block specific agent versions from connecting, along with expanded data retention configuration options.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Install progress</span> — The agent install progress page now shows download and install phases with elapsed time.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">TPM info collection</span> — TPM details are now collected at enrollment time for improved hardware diagnostics.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Firewall compatibility</span> — The agent now sends a dedicated User-Agent header to simplify firewall allowlisting.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-01 - 16:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Pre-Provisioning (WhiteGlove)
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Ongoing improvements to Pre-Provisioning support (still testing)
              </h2>
              <p className="text-sm text-gray-600 leading-relaxed">
                I'm continuously improving support for Pre-Provisioning (White Glove) scenarios.
                The session timeline should now better reflect the provisioning process better, and I'm 
                working on improving the accuracy of event categorization and timing for these sessions.
                If you are using Pre-Provisioning and notice any discrepancies in the timeline or data, 
                please share your Feedback with me via GitHub Issues. Your feedback is invaluable in 
                helping me enhance support for these scenarios.
                Expect a "Report Session" button in the timeline view soon to make sharing feedback and logs easier!
              </p>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-02-27 - 21:38 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Features
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Configurable Diagnostic Package, Gather Rule Examples, Updated Docs
              </h2>
              <p className="text-sm text-gray-600 leading-relaxed">
                The configurable diagnostic package allows for more flexible data collection and analysis.
                Gather rule examples have been added to help users understand how to create their own rules.
                Documentation has been updated to reflect these changes and provide guidance on using the features.
              </p>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-02-27 - 14:38 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Architecture
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                First implementation of Pre-Provisioning support incl. session timeline visualization
              </h2>
              <p className="text-sm text-gray-600 leading-relaxed">
                The session timeline now also supports sessions that started with Pre-Provisioning
                (aka White Glove) — including the provisioning process itself. This is a first
                implementation and only tested with a very basic scenario, so if you use
                Pre-Provisioning and see anything that looks off in the timeline, please check
                the logs and share them via GitHub Issues.
              </p>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-02-26 - 10:15 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Architecture
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Reworked real-time event delivery and session timeline processing
              </h2>
              <p className="text-sm text-gray-600 leading-relaxed">
                The way live session events reach the dashboard timeline was fundamentally
                reworked. This should make the timeline more reliable and accurate.
              </p>
            </div>

          </div>

        </div>
      </main>
    </div>
  );
}
