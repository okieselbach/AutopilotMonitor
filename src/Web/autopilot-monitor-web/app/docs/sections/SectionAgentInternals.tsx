"use client";

/* ------------------------------------------------------------------ */
/*  Helper: collapsible sub-section                                    */
/* ------------------------------------------------------------------ */
function SubSection({ title, icon, color, children }: {
  title: string;
  icon: React.ReactNode;
  color: string;
  children: React.ReactNode;
}) {
  return (
    <div className="mb-8">
      <div className="flex items-center gap-2 mb-3">
        {icon}
        <h3 className="text-lg font-semibold text-gray-900">{title}</h3>
      </div>
      <div className={`border border-${color}-200 rounded-lg overflow-hidden`}>
        {children}
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Helper: simple key-value table                                     */
/* ------------------------------------------------------------------ */
function DataTable({ headers, rows }: { headers: string[]; rows: string[][] }) {
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead>
          <tr className="bg-gray-50">
            {headers.map((h) => (
              <th key={h} className="px-4 py-2 text-left font-semibold text-gray-700 border-b border-gray-200">{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => (
            <tr key={i} className={i % 2 === 0 ? "bg-white" : "bg-gray-50"}>
              {row.map((cell, j) => (
                <td key={j} className="px-4 py-2 text-gray-700 border-b border-gray-100">
                  {j === 0 ? <span className="font-mono text-xs">{cell}</span> : cell}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Helper: numbered step                                              */
/* ------------------------------------------------------------------ */
function Step({ n, color, title, children }: { n: number; color: string; title: string; children: React.ReactNode }) {
  return (
    <div className={`flex items-start gap-3 p-3 bg-${color}-50 border border-${color}-100 rounded-lg`}>
      <span className={`text-${color}-600 font-bold mt-0.5 shrink-0`}>{n}</span>
      <div>
        <span className="font-medium text-gray-900">{title}</span>{" "}
        <span className="text-gray-700">{children}</span>
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Helper: info box                                                   */
/* ------------------------------------------------------------------ */
function InfoBox({ color, children }: { color: string; children: React.ReactNode }) {
  return (
    <div className={`p-4 bg-${color}-50 border border-${color}-200 rounded-lg text-sm text-${color}-800`}>
      {children}
    </div>
  );
}

/* ================================================================== */
/*  MAIN COMPONENT                                                     */
/* ================================================================== */
export function SectionAgentInternals() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">

      {/* ── Header ─────────────────────────────────────── */}
      <div className="flex items-center space-x-3 mb-2">
        <svg className="w-8 h-8 text-indigo-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8.25 3v1.5M4.5 8.25H3m18 0h-1.5M4.5 12H3m18 0h-1.5M4.5 15.75H3m18 0h-1.5M8.25 19.5V21m0-18v1.5m0 15V21m4.5-18v1.5m0 15V21M16.5 3v1.5m0 15V21M6.75 6.75h10.5v10.5H6.75V6.75z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Agent Internals</h2>
      </div>
      <p className="text-gray-500 text-sm mb-6">
        Technical reference of all agent capabilities. Visible to Global Admins only.
      </p>
      <p className="text-gray-700 mb-8">
        The Autopilot Monitor agent is a .NET Framework 4.8 application that runs as a SYSTEM-level
        scheduled task during Windows Autopilot enrollment. This page documents every capability, event
        type, configuration setting, and architectural detail of the agent.
      </p>

      {/* ── Table of Contents ──────────────────────────── */}
      <div className="mb-10 p-5 bg-gray-50 border border-gray-200 rounded-lg">
        <h3 className="font-semibold text-gray-900 mb-3">Contents</h3>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-1 text-sm">
          {[
            "1. Deployment & Bootstrap",
            "2. Authentication & Security",
            "3. Configuration Management",
            "4. Enrollment Monitoring",
            "5. Device Information Collection",
            "6. Runtime Monitoring",
            "7. Network Monitoring",
            "8. Desktop Arrival Detection",
            "9. Security Analyzers",
            "10. Gather Rules System",
            "11. Geo-Location & Time Services",
            "12. Diagnostics Package",
            "13. Self-Update Mechanism",
            "14. Event Upload & Resilience",
            "15. Session Management & Crash Recovery",
            "16. Enrollment Summary Dialog",
            "17. Cleanup & Self-Destruct",
            "18. Special Execution Modes",
            "19. Logging",
            "20. Complete Event Type Reference",
            "21. API Endpoints",
          ].map((item) => (
            <span key={item} className="text-gray-600">{item}</span>
          ))}
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          1. DEPLOYMENT & BOOTSTRAP
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15.59 14.37a6 6 0 01-5.84 7.38v-4.8m5.84-2.58a14.98 14.98 0 006.16-12.12A14.98 14.98 0 009.631 8.41m5.96 5.96a14.926 14.926 0 01-5.841 2.58m-.119-8.54a6 6 0 00-7.381 5.84h4.8m2.581-5.84a14.927 14.927 0 00-2.58 5.84m2.699 2.7c-.103.021-.207.041-.311.06a15.09 15.09 0 01-2.448-2.448 14.9 14.9 0 01.06-.312m-2.24 2.39a4.493 4.493 0 00-1.757 4.306 4.493 4.493 0 004.306-1.758M16.5 9a1.5 1.5 0 11-3 0 1.5 1.5 0 013 0z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">1. Deployment & Bootstrap</h3>
        </div>

        <div className="space-y-4 mb-4">
          <div className="border border-blue-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-blue-50 border-b border-blue-200">
              <p className="font-semibold text-sm text-blue-900">Installation Mode (--install)</p>
            </div>
            <div className="px-4 py-3 space-y-2 text-sm text-gray-700">
              <p>Copies agent payload to <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">%ProgramData%\AutopilotMonitor\Agent\</span></p>
              <p>Creates directory structure: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">Agent, Config, Logs, Spool, State</span></p>
              <p>Registers Windows Scheduled Task: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">AutopilotMonitor-Agent</span> (ONSTART, SYSTEM, HIGHEST)</p>
              <p>Starts task immediately after registration</p>
              <p>Writes deployment marker: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">HKLM\SOFTWARE\AutopilotMonitor\Deployed</span></p>
            </div>
          </div>

          <div className="border border-blue-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-blue-50 border-b border-blue-200">
              <p className="font-semibold text-sm text-blue-900">Bootstrap Token Auth (Pre-MDM)</p>
            </div>
            <div className="px-4 py-3 space-y-2 text-sm text-gray-700">
              <p>Before MDM enrollment completes, the agent authenticates via <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">X-Bootstrap-Token</span> header</p>
              <p>Bootstrap config persisted in <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">bootstrap-config.json</span> (Token + TenantId)</p>
              <p>Automatic switch to mTLS once MDM certificate appears in cert store</p>
              <p>Bootstrap config deleted after switch &mdash; no secrets remain on disk</p>
            </div>
          </div>

          <div className="border border-blue-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-blue-50 border-b border-blue-200">
              <p className="font-semibold text-sm text-blue-900">Await-Enrollment Mode</p>
            </div>
            <div className="px-4 py-3 space-y-2 text-sm text-gray-700">
              <p>Polls for MDM certificate in <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">LocalMachine\My</span> store every 5 seconds</p>
              <p>Configurable timeout: default 480 minutes (8 hours)</p>
              <p>Reads TenantId from registry after certificate appears</p>
            </div>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          2. AUTHENTICATION & SECURITY
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-green-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">2. Authentication & Security</h3>
        </div>

        <div className="space-y-3 text-sm text-gray-700 mb-4">
          <Step n={1} color="green" title="Client Certificate (mTLS):">
            Searches Intune MDM certificate in LocalMachine\My, fallback CurrentUser\My.
            Filters by issuer containing &quot;Microsoft Intune&quot; / &quot;MDM Device CA&quot;.
            Validates Enhanced Key Usage (Client Authentication OID). Selects longest validity.
          </Step>
          <Step n={2} color="green" title="Hardware Identity Headers:">
            Every request includes X-Device-Manufacturer, X-Device-Model, X-Device-SerialNumber,
            X-Agent-Version, X-Tenant-Id. Lenovo: special handling via Win32_ComputerSystemProduct.
          </Step>
          <Step n={3} color="green" title="Auth Failure Circuit Breaker:">
            MaxAuthFailures (default: 5) consecutive 401/403 responses trigger agent shutdown.
            Prevents infinite retry traffic against the backend.
          </Step>
          <Step n={4} color="green" title="Security Audit Event:">
            Reports UnrestrictedMode status, BypassNRO flag, and other security indicators at startup.
            UnrestrictedMode is never cached to disk &mdash; requires live backend auth each time.
          </Step>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          3. CONFIGURATION MANAGEMENT
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-purple-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.324.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 011.37.49l1.296 2.247a1.125 1.125 0 01-.26 1.431l-1.003.827c-.293.24-.438.613-.431.992a6.759 6.759 0 010 .255c-.007.378.138.75.43.99l1.005.828c.424.35.534.954.26 1.43l-1.298 2.247a1.125 1.125 0 01-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.57 6.57 0 01-.22.128c-.331.183-.581.495-.644.869l-.213 1.28c-.09.543-.56.941-1.11.941h-2.594c-.55 0-1.02-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 01-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 01-1.369-.49l-1.297-2.247a1.125 1.125 0 01.26-1.431l1.004-.827c.292-.24.437-.613.43-.992a6.932 6.932 0 010-.255c.007-.378-.138-.75-.43-.99l-1.004-.828a1.125 1.125 0 01-.26-1.43l1.297-2.247a1.125 1.125 0 011.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.087.22-.128.332-.183.582-.495.644-.869l.214-1.281z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">3. Configuration Management</h3>
        </div>

        <p className="text-sm text-gray-700 mb-4">
          The agent fetches its configuration from <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">/api/agent/config</span> at
          startup with a three-level fallback chain: Backend &rarr; Cached <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">remote-config.json</span> &rarr; Hardcoded defaults.
          Over 50 settings are remotely configurable.
        </p>

        <DataTable
          headers={["Category", "Settings", "Defaults"]}
          rows={[
            ["Upload", "UploadIntervalSeconds, MaxBatchSize, MaxRetryAttempts", "30s, 100, 5"],
            ["Collector Toggles", "EnablePerformanceCollector, EnableAgentSelfMetrics, EnableDeliveryOptimization", "varies"],
            ["Collector Timing", "PerformanceIntervalSeconds, AgentSelfMetricsIntervalSeconds, DOIntervalSeconds", "30s, 60s, 5s"],
            ["Idle Management", "CollectorIdleTimeoutMinutes", "15 min"],
            ["Logging", "LogLevel, SendTraceEvents", "Info, true"],
            ["Geo / Time", "EnableGeoLocation, EnableTimezoneAutoSet, NtpServer", "true, false, time.windows.com"],
            ["Diagnostics", "DiagnosticsUploadEnabled, DiagnosticsUploadMode, DiagnosticsLogPaths", "false, Off, []"],
            ["Lifetime Safety", "AgentMaxLifetimeMinutes, AbsoluteMaxSessionHours", "360 (6h), 48"],
            ["Hello", "HelloWaitTimeoutSeconds", "30s"],
            ["Cleanup", "SelfDestructOnComplete, KeepLogFile, RebootOnComplete, RebootDelaySeconds", "true, false, false, 10"],
            ["Enrollment Summary", "ShowEnrollmentSummary, EnrollmentSummaryTimeoutSeconds, BrandingImageUrl", "false, 0, null"],
            ["Analyzers", "EnableLocalAdminAnalyzer, EnableSoftwareInventoryAnalyzer, LocalAdminAllowedAccounts", "true, false, []"],
            ["Auth Resilience", "MaxAuthFailures, AuthFailureTimeoutMinutes", "5, 0"],
            ["Gather Rules", "List of dynamic rules (registry, wmi, eventlog, file, command, logparser)", "[]"],
            ["IME Patterns", "Backend-supplied regex patterns for IME log parsing", "[]"],
          ]}
        />
      </div>

      {/* ═══════════════════════════════════════════════════
          4. ENROLLMENT MONITORING
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-indigo-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.75 6A2.25 2.25 0 016 3.75h2.25A2.25 2.25 0 0110.5 6v2.25a2.25 2.25 0 01-2.25 2.25H6a2.25 2.25 0 01-2.25-2.25V6zM3.75 15.75A2.25 2.25 0 016 13.5h2.25a2.25 2.25 0 012.25 2.25V18a2.25 2.25 0 01-2.25 2.25H6A2.25 2.25 0 013.75 18v-2.25zM13.5 6a2.25 2.25 0 012.25-2.25H18A2.25 2.25 0 0120.25 6v2.25A2.25 2.25 0 0118 10.5h-2.25a2.25 2.25 0 01-2.25-2.25V6zM13.5 15.75a2.25 2.25 0 012.25-2.25H18a2.25 2.25 0 012.25 2.25V18A2.25 2.25 0 0118 20.25h-2.25A2.25 2.25 0 0113.5 18v-2.25z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">4. Enrollment Monitoring (Core Feature)</h3>
        </div>

        {/* ESP Tracking */}
        <div className="border border-indigo-200 rounded-lg overflow-hidden mb-4">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">ESP (Enrollment Status Page) Tracking</p>
          </div>
          <div className="px-4 py-3 space-y-2 text-sm text-gray-700">
            <p><span className="font-medium">Phase Detection:</span> DeviceSetup &rarr; AccountSetup via IME log patterns + registry monitoring</p>
            <p><span className="font-medium">Forward-Only Progression:</span> Phases cannot regress (prevents accidental phase bounce)</p>
            <p><span className="font-medium">ESP Exit Detection:</span> Shell-Core Event 62407 (CommercialOOBE_ESPProgress_Page_Exiting)</p>
            <p><span className="font-medium">ESP Failure Detection:</span> Event 360 with 60-second grace period for transient failures</p>
            <p><span className="font-medium">Device-Only ESP:</span> 5-minute timer &mdash; if no AccountSetup appears, classified as device-only (self-deploying)</p>
            <p><span className="font-medium">ESP Config Detection:</span> Reads skip_user_status_page / skip_device_status_page registry flags</p>
            <p><span className="font-medium">Provisioning Status:</span> Registry polling of DeviceSetup/AccountSetup progress</p>
          </div>
        </div>

        {/* Hello Tracking */}
        <div className="border border-indigo-200 rounded-lg overflow-hidden mb-4">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">Windows Hello for Business Tracking</p>
          </div>
          <div className="px-4 py-3 space-y-2 text-sm text-gray-700">
            <p><span className="font-medium">Event Log Monitoring:</span> User Device Registration events 300, 301, 358, 360, 362</p>
            <p><span className="font-medium">Shell-Core Monitoring:</span> Events 62404/62407 for Hello wizard start/end</p>
            <p><span className="font-medium">Policy Detection:</span> PassportForWork registry paths (CSP + GPO)</p>
            <p><span className="font-medium">Hello Wait Timeout:</span> Configurable timeout after ESP exit (default: 30s)</p>
            <p><span className="font-medium">Hello Completion Timeout:</span> 300 seconds (5 min) from wizard start</p>
            <p><span className="font-medium">Outcomes:</span> completed, failed, timeout, not_launched, blocked, wizard_timeout</p>
          </div>
        </div>

        {/* IME Log Tracking */}
        <div className="border border-indigo-200 rounded-lg overflow-hidden mb-4">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">IME (Intune Management Extension) Log Tracking</p>
          </div>
          <div className="px-4 py-3 space-y-2 text-sm text-gray-700">
            <p><span className="font-medium">Pattern-Based:</span> All regex patterns come from the backend &mdash; no hardcoded patterns. Allows updates without agent rebuild.</p>
            <p><span className="font-medium">Log Files Monitored:</span> IntuneManagementExtension.log, AppWorkload.log, AgentExecutor.log, HealthScripts.log (including rotated archives)</p>
            <p><span className="font-medium">App Lifecycle:</span> download_started &rarr; downloading &rarr; installing &rarr; installed / failed / skipped / postponed</p>
            <p><span className="font-medium">Script Execution:</span> Platform Scripts + Health Scripts (Proactive Remediations). Health scripts emit a live <code className="text-xs">script_started</code> indicator on policy start and up to three <code className="text-xs">script_completed</code> events per cycle (pre-detection / remediation / post-detection) parsed from the consolidated <code className="text-xs">[HS] new result</code> JSON line — exit code, stdout, stderr, compliance status, RemediationStatus.</p>
            <p><span className="font-medium">DO Telemetry:</span> Per-app Delivery Optimization data (bytes from peers/HTTP/LAN, peer caching %)</p>
            <p><span className="font-medium">Phase Isolation:</span> Device-phase apps do not bleed into AccountSetup tracking</p>
            <p><span className="font-medium">State Persistence:</span> Log positions + app states survive agent crashes/restarts</p>
          </div>
        </div>

        {/* Completion Logic */}
        <div className="border border-indigo-200 rounded-lg overflow-hidden mb-4">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">Enrollment Completion (Multi-Signal)</p>
          </div>
          <div className="px-4 py-3 space-y-2 text-sm text-gray-700">
            <p className="font-medium mb-2">Three independent completion paths:</p>
            <div className="space-y-2">
              <Step n={1} color="indigo" title="IME Pattern Match:">Existing pattern-based detection from IME logs</Step>
              <Step n={2} color="indigo" title="ESP Final Exit + Hello:">Composite signal from ESP exit event + Hello provisioning</Step>
              <Step n={3} color="indigo" title="Desktop Arrival + Hello:">For scenarios without ESP (e.g., WDP v2)</Step>
            </div>
            <p className="mt-3"><span className="font-medium">Signal Audit Trail:</span> Terminal events include signalsSeen + signalTimestamps for debugging</p>
            <p><span className="font-medium">Completion Checks:</span> Throttled (1x/min/source) with full state machine snapshot</p>
            <p><span className="font-medium">Enrollment Types:</span> v1 (Classic/ESP) vs v2 (Windows Device Preparation)</p>
          </div>
        </div>

        {/* WhiteGlove */}
        <div className="border border-indigo-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">WhiteGlove / Pre-Provisioning</p>
          </div>
          <div className="px-4 py-3 space-y-2 text-sm text-gray-700">
            <p>Part 1 detection via Shell-Core event (WhiteGlove_Success)</p>
            <p>Part 1 complete marker persisted to disk for crash recovery</p>
            <p>Part 2 resume detection on next agent start</p>
            <p>Separate session handling per part</p>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          5. DEVICE INFORMATION COLLECTION
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17.25v1.007a3 3 0 01-.879 2.122L7.5 21h9l-.621-.621A3 3 0 0115 18.257V17.25m6-12V15a2.25 2.25 0 01-2.25 2.25H5.25A2.25 2.25 0 013 15V5.25m18 0A2.25 2.25 0 0018.75 3H5.25A2.25 2.25 0 003 5.25m18 0V12a2.25 2.25 0 01-2.25 2.25H5.25A2.25 2.25 0 013 12V5.25" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">5. Device Information Collection</h3>
        </div>
        <p className="text-sm text-gray-600 mb-3">Collected at startup and at enrollment start. Sources: WMI, Registry, Event Log.</p>

        <DataTable
          headers={["Event Type", "Data Source", "Content"]}
          rows={[
            ["os_info", "WMI + Registry", "Windows version, build, edition, release, UBR"],
            ["boot_time", "Event Log", "System boot timestamp"],
            ["hardware_spec", "WMI", "CPU, RAM, disk, manufacturer, model, VM detection"],
            ["tpm_status", "WMI Win32_Tpm", "TPM version, spec, ready status"],
            ["secureboot_status", "WMI", "UEFI Secure Boot enabled/disabled"],
            ["bitlocker_status", "WMI Win32_EncryptableVolume", "BitLocker encryption status"],
            ["aad_join_status", "Registry + WMI", "Azure AD join state, user email, tenant ID"],
            ["autopilot_profile", "Registry", "Autopilot registration, group tag, deployment mode"],
            ["enrollment_type_detected", "Registry", "v1 (Classic/ESP) vs v2 (WDP)"],
            ["esp_config_detected", "Registry", "ESP skip flags"],
            ["network_adapters", "WMI", "All NICs: name, type, speed"],
            ["dns_configuration", "WMI", "DNS servers per NIC"],
            ["proxy_configuration", "Registry", "WinHTTP proxy settings"],
            ["active_network_interface", "WMI + netsh", "Active NIC details, WiFi SSID, signal strength"],
          ]}
        />
      </div>

      {/* ═══════════════════════════════════════════════════
          6. RUNTIME MONITORING
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-amber-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 13.125C3 12.504 3.504 12 4.125 12h2.25c.621 0 1.125.504 1.125 1.125v6.75C7.5 20.496 6.996 21 6.375 21h-2.25A1.125 1.125 0 013 19.875v-6.75zM9.75 8.625c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125v11.25c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V8.625zM16.5 4.125c0-.621.504-1.125 1.125-1.125h2.25C20.496 3 21 3.504 21 4.125v15.75c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V4.125z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">6. Runtime Monitoring</h3>
        </div>

        <div className="space-y-4">
          <div className="border border-amber-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-amber-50 border-b border-amber-200">
              <p className="font-semibold text-sm text-amber-900">Performance Collector (Optional)</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>System CPU %, memory (working set, committed), disk queue length, network throughput</p>
              <p>Interval: configurable (default 30s) &mdash; Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">performance_snapshot</span></p>
            </div>
          </div>

          <div className="border border-amber-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-amber-50 border-b border-amber-200">
              <p className="font-semibold text-sm text-amber-900">Agent Self-Metrics Collector</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>Agent process: CPU %, working set, private bytes, thread count, handle count</p>
              <p>HTTP network traffic: requests sent, bytes up/down, latency</p>
              <p>Uses pure Process properties (no WMI, no PerformanceCounters)</p>
              <p>Interval: 60s &mdash; Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">agent_metrics_snapshot</span></p>
            </div>
          </div>

          <div className="border border-amber-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-amber-50 border-b border-amber-200">
              <p className="font-semibold text-sm text-amber-900">Delivery Optimization Collector (Optional)</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>DO service status, peer connections, download stats via PowerShell</p>
              <p>Interval: 5s &mdash; Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">do_status_snapshot</span></p>
            </div>
          </div>

          <InfoBox color="amber">
            <span className="font-medium">Idle Management:</span> Periodic collectors auto-stop after 15 minutes without real enrollment
            activity. &quot;Real&quot; events = everything except performance_snapshot and agent_metrics_snapshot.
            Collectors restart automatically when new activity arrives.
          </InfoBox>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          7. NETWORK MONITORING
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-cyan-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 21a9.004 9.004 0 008.716-6.747M12 21a9.004 9.004 0 01-8.716-6.747M12 21c2.485 0 4.5-4.03 4.5-9S14.485 3 12 3m0 18c-2.485 0-4.5-4.03-4.5-9S9.515 3 12 3m0 0a8.997 8.997 0 017.843 4.582M12 3a8.997 8.997 0 00-7.843 4.582m15.686 0A11.953 11.953 0 0112 10.5c-2.998 0-5.74-1.1-7.843-2.918m15.686 0A8.959 8.959 0 0121 12c0 .778-.099 1.533-.284 2.253m0 0A17.919 17.919 0 0112 16.5c-3.162 0-6.133-.815-8.716-2.247m0 0A9.015 9.015 0 013 12c0-1.605.42-3.113 1.157-4.418" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">7. Network Monitoring</h3>
        </div>

        <div className="space-y-4">
          <div className="border border-cyan-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-cyan-50 border-b border-cyan-200">
              <p className="font-semibold text-sm text-cyan-900">Network Change Detection (Always-On)</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>Detects: SSID switches, wired &harr; wireless transitions, IP changes, adapter changes</p>
              <p>3-second debounce to suppress OS chatter</p>
              <p>WiFi details: SSID, signal strength, radio type (via netsh wlan)</p>
              <p>Events: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">network_state_change</span></p>
            </div>
          </div>

          <div className="border border-cyan-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-cyan-50 border-b border-cyan-200">
              <p className="font-semibold text-sm text-cyan-900">MDM Endpoint Connectivity Check</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>Triggered after every network change. Tests connectivity to:</p>
              <ul className="list-disc list-inside ml-2 space-y-0.5">
                <li>login.microsoftonline.com (Entra ID)</li>
                <li>enterpriseregistration.windows.net (Device Registration)</li>
                <li>portal.manage.microsoft.com (Intune)</li>
                <li>graph.microsoft.com (Microsoft Graph)</li>
              </ul>
              <p>Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">network_connectivity_check</span></p>
            </div>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          8. DESKTOP ARRIVAL DETECTION
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-teal-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17.25v1.007a3 3 0 01-.879 2.122L7.5 21h9l-.621-.621A3 3 0 0115 18.257V17.25m6-12V15a2.25 2.25 0 01-2.25 2.25H5.25A2.25 2.25 0 013 15V5.25m18 0A2.25 2.25 0 0018.75 3H5.25A2.25 2.25 0 003 5.25m18 0V12a2.25 2.25 0 01-2.25 2.25H5.25A2.25 2.25 0 013 12V5.25" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">8. Desktop Arrival Detection</h3>
        </div>
        <div className="p-4 bg-teal-50 border border-teal-200 rounded-lg text-sm text-gray-700 space-y-2">
          <p>Polls <span className="font-mono text-xs bg-teal-100 px-1 py-0.5 rounded">explorer.exe</span> every 30 seconds</p>
          <p>Validates user via WMI GetOwner() &mdash; excludes SYSTEM, LOCAL SERVICE, NETWORK SERVICE, DefaultUser*</p>
          <p>Fires <span className="font-mono text-xs bg-teal-100 px-1 py-0.5 rounded">desktop_arrived</span> event exactly once per session</p>
          <p>Used for no-ESP completion signal and AccountSetup phase correction</p>
          <p>WDP v2: Desktop arrival gate is skipped (no ESP in WDP v2)</p>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          9. SECURITY ANALYZERS
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-red-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">9. Security Analyzers</h3>
        </div>

        <div className="space-y-4">
          <div className="border border-red-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-red-50 border-b border-red-200">
              <p className="font-semibold text-sm text-red-900">Local Admin Analyzer</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-2">
              <p>Detects unexpected local administrator accounts and Autopilot bypass techniques</p>
              <p><span className="font-medium">Checks:</span> BypassNRO registry flag, unexpected local accounts (WMI), unexpected C:\Users profiles</p>
              <p><span className="font-medium">Confidence Scoring:</span> BypassNRO (+20), unexpected account (+40), account+profile overlap (+40)</p>
              <p>Tenant-supplied account allowlists (merged with built-in exclusions)</p>
              <p>Dynamic allowlisting of logged-in users at shutdown</p>
              <p>Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">local_admin_analysis</span> at startup + shutdown</p>
            </div>
          </div>

          <div className="border border-red-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-red-50 border-b border-red-200">
              <p className="font-semibold text-sm text-red-900">Software Inventory Analyzer (Optional)</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-2">
              <p>Baseline at startup, delta detection at shutdown</p>
              <p><span className="font-medium">Sources:</span> HKLM 64/32-bit, HKCU, HKU per-user profiles, AppX/MSIX packages</p>
              <p>Publisher normalization (75+ mappings), product &amp; version normalization</p>
              <p>Noise filter: KB updates, language packs, .NET runtimes, system components</p>
              <p>Strict AppX whitelist (Company Portal, Teams, Windows Terminal, etc.)</p>
              <p>Chunked upload: 75 items/event (Table Storage limits)</p>
              <p>Enables server-side CVE/KEV correlation</p>
              <p>Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">software_inventory_analysis</span></p>
            </div>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          10. GATHER RULES SYSTEM
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-violet-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8.25 6.75h12M8.25 12h12m-12 5.25h12M3.75 6.75h.007v.008H3.75V6.75zm.375 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zM3.75 12h.007v.008H3.75V12zm.375 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm-.375 5.25h.007v.008H3.75v-.008zm.375 0a.375.375 0 11-.75 0 .375.375 0 01.75 0z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">10. Gather Rules System</h3>
        </div>

        <p className="text-sm text-gray-700 mb-3">
          Backend-defined dynamic data collection rules. Each rule specifies a collector type, target, trigger, and output format.
        </p>

        <DataTable
          headers={["Collector Type", "Description", "Example Target"]}
          rows={[
            ["registry", "Read registry keys with filter/transform", "HKLM\\SOFTWARE\\Microsoft\\..."],
            ["wmi", "Execute WMI queries", "SELECT * FROM Win32_OperatingSystem"],
            ["command_allowlisted", "Run allowlisted commands", "PowerShell commands"],
            ["file", "Read/parse files (txt, json, xml, log, csv)", "C:\\Windows\\INF\\setupapi.dev.log"],
            ["eventlog", "Query Windows Event Logs", "Microsoft-Windows-AAD/Operational"],
            ["logparser", "Parse text logs with regex", "Custom log files"],
          ]}
        />

        <div className="mt-4 space-y-3">
          <div className="p-3 bg-violet-50 border border-violet-100 rounded-lg text-sm">
            <span className="font-medium text-gray-900">Triggers:</span>
            <span className="text-gray-700"> startup, phase_change (ESP phase transition), interval (periodic), on_event (reactive)</span>
          </div>
          <div className="p-3 bg-violet-50 border border-violet-100 rounded-lg text-sm">
            <span className="font-medium text-gray-900">Security Guardrails:</span>
            <span className="text-gray-700"> Registry path whitelist/blacklist, WMI class/property allowlists, file/command path guards. UnrestrictedMode override available but never persisted to disk.</span>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          11. GEO-LOCATION & TIME SERVICES
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-emerald-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6h4.5m4.5 0a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">11. Geo-Location & Time Services</h3>
        </div>

        <div className="space-y-4">
          <div className="border border-emerald-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-emerald-50 border-b border-emerald-200">
              <p className="font-semibold text-sm text-emerald-900">Geo-Location</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>IP-based lookup: ipinfo.io (primary), ifconfig.co (fallback). 5s timeout, retry with 2s delay.</p>
              <p>Data: country, region, city, coordinates, timezone</p>
              <p>Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">device_location</span> &mdash; Enabled by default (toggle via config)</p>
            </div>
          </div>
          <div className="border border-emerald-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-emerald-50 border-b border-emerald-200">
              <p className="font-semibold text-sm text-emerald-900">NTP Time Check</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>NTP v3 client mode, 48-byte protocol. Default server: time.windows.com. 8s socket timeout.</p>
              <p>Calculates local-to-NTP clock offset for enrollment prerequisite validation</p>
              <p>Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">ntp_time_check</span></p>
            </div>
          </div>
          <div className="border border-emerald-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-emerald-50 border-b border-emerald-200">
              <p className="font-semibold text-sm text-emerald-900">Timezone Auto-Set</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>Sets Windows timezone via <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">tzutil /s</span> based on geo-location result</p>
              <p>Requires SYSTEM privileges. Disabled by default (toggle via config).</p>
              <p>Event: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">timezone_auto_set</span></p>
            </div>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          12. DIAGNOSTICS PACKAGE
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-orange-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19.5 14.25v-2.625a3.375 3.375 0 00-3.375-3.375h-1.5A1.125 1.125 0 0113.5 7.125v-1.5a3.375 3.375 0 00-3.375-3.375H8.25m0 12.75h7.5m-7.5 3H12M10.5 2.25H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 00-9-9z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">12. Diagnostics Package</h3>
        </div>
        <div className="space-y-4">
          <div className="p-4 bg-orange-50 border border-orange-200 rounded-lg text-sm text-gray-700 space-y-2">
            <p>Creates ZIP archive with agent logs, IME logs, and session info</p>
            <p>Configurable log paths from backend (global + tenant merged) with built-in + custom paths</p>
            <p>Fetches short-lived SAS URL via <span className="font-mono text-xs bg-orange-100 px-1 py-0.5 rounded">/api/diagnostics/upload-url</span> &mdash; never stored on disk</p>
            <p>Uploads to Azure Blob Storage, then deletes local ZIP</p>
            <p>Modes: <span className="font-mono text-xs bg-orange-100 px-1 py-0.5 rounded">Off</span> (default), <span className="font-mono text-xs bg-orange-100 px-1 py-0.5 rounded">Always</span>, <span className="font-mono text-xs bg-orange-100 px-1 py-0.5 rounded">OnFailure</span></p>
            <p>WhiteGlove support: part suffix for ZIP files (e.g., <span className="font-mono text-xs bg-orange-100 px-1 py-0.5 rounded">-preprov.zip</span>)</p>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          13. SELF-UPDATE MECHANISM
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-sky-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182M20.996 19.632h-4.991" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">13. Self-Update Mechanism</h3>
        </div>

        <div className="space-y-3 text-sm text-gray-700">
          <Step n={1} color="sky" title="Version Check (1s timeout):">
            Fetches version.json from blob storage. Compares against current agent version.
          </Step>
          <Step n={2} color="sky" title="Download (10s timeout):">
            Downloads agent ZIP. SHA-256 hash validation (backend hash has priority, fallback to version.json hash). Aborts on mismatch.
          </Step>
          <Step n={3} color="sky" title="File Swap:">
            Rename trick for locked binaries (.old suffix). Copies staged EXE to Agent directory.
          </Step>
          <Step n={4} color="sky" title="Restart:">
            Writes self-update marker, restarts via PowerShell Wait-Process. Next startup emits agent_version_check event (outcome=updated).
          </Step>
        </div>
        <InfoBox color="sky">
          <span className="font-medium">Priority: Speed over update.</span> 1-second version check, 10-second download timeout.
          Any failure continues with the current version &mdash; never blocks startup.
        </InfoBox>
      </div>

      {/* ═══════════════════════════════════════════════════
          14. EVENT UPLOAD & RESILIENCE
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-rose-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 16.5V9.75m0 0l3 3m-3-3l-3 3M6.75 19.5a4.5 4.5 0 01-1.41-8.775 5.25 5.25 0 0110.233-2.33 3 3 0 013.758 3.848A3.752 3.752 0 0118 19.5H6.75z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">14. Event Upload & Resilience</h3>
        </div>

        <div className="space-y-4">
          <div className="border border-rose-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-rose-50 border-b border-rose-200">
              <p className="font-semibold text-sm text-rose-900">Batching & Compression</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>NDJSON + gzip compression (70&ndash;80% reduction)</p>
              <p>MaxBatchSize: 100 events per batch, upload interval: 30s (configurable)</p>
              <p>Debounce timer for rapid event bursts</p>
              <p>Sequence numbering for ordering + deduplication</p>
            </div>
          </div>

          <div className="border border-rose-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-rose-50 border-b border-rose-200">
              <p className="font-semibold text-sm text-rose-900">Offline Queueing (Event Spool)</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700 space-y-1">
              <p>File-based persistence: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">Spool/event_*.json</span></p>
              <p>FileSystemWatcher for automatic upload triggering</p>
              <p>Crash recovery via sequence ceiling detection</p>
            </div>
          </div>

          <DataTable
            headers={["Channel", "Auth", "Endpoint", "Anti-Flood", "Use Case"]}
            rows={[
              ["Normal Upload", "mTLS / Bootstrap", "/api/agent/ingest", "Retry + backoff", "Regular events"],
              ["Emergency Reporter", "mTLS / Bootstrap", "/api/agent/error", "5/session, 10min min", "Config-fetch / upload failures"],
              ["Distress Reporter", "None (plain HTTP)", "/api/agent/distress", "3/session, 30min min", "Cert missing, auth failures"],
            ]}
          />

          <div className="p-3 bg-rose-50 border border-rose-100 rounded-lg text-sm text-gray-700 space-y-1">
            <p><span className="font-medium">Rate Limiting:</span> Backend can return RateLimitExceeded + RateLimitInfo &mdash; agent throttles uploads</p>
            <p><span className="font-medium">Device Block/Kill:</span> Backend can send DeviceBlocked + DeviceKillSignal &mdash; agent stops or adapts</p>
            <p><span className="font-medium">Admin Override:</span> Backend can send AdminAction (pre-marked Succeeded/Failed) &mdash; agent accepts and runs cleanup</p>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          15. SESSION MANAGEMENT & CRASH RECOVERY
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-fuchsia-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M20.25 6.375c0 2.278-3.694 4.125-8.25 4.125S3.75 8.653 3.75 6.375m16.5 0c0-2.278-3.694-4.125-8.25-4.125S3.75 4.097 3.75 6.375m16.5 0v11.25c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125V6.375m16.5 0v3.75m-16.5-3.75v3.75m16.5 0v3.75C20.25 16.153 16.556 18 12 18s-8.25-1.847-8.25-4.125v-3.75m16.5 0c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">15. Session Management & Crash Recovery</h3>
        </div>

        <DataTable
          headers={["File", "Content"]}
          rows={[
            ["session.id", "Session GUID (persistent across restarts)"],
            ["session.created", "Creation timestamp (orphan guard)"],
            ["session.seq", "Last event sequence counter (crash recovery)"],
            ["whiteglove.complete", "Part 1 complete marker"],
            ["clean-exit.marker", "Clean shutdown marker"],
            ["ime-tracker-state.json", "IME tracker state (log positions, app states)"],
          ]}
        />

        <div className="mt-4 space-y-3 text-sm text-gray-700">
          <div className="p-3 bg-fuchsia-50 border border-fuchsia-100 rounded-lg">
            <span className="font-medium text-gray-900">Crash Recovery:</span> Sequence = max(persisted sequence, spool ceiling).
            IME state persistence (log positions + app states) survives hard kills and BSODs.
          </div>
          <div className="p-3 bg-fuchsia-50 border border-fuchsia-100 rounded-lg">
            <span className="font-medium text-gray-900">Clean Exit Detection:</span> ProcessExit handler writes clean-exit.marker.
            Previous exit classified as: clean, crash, hard_kill, reboot_kill, or first_run.
          </div>
          <div className="p-3 bg-fuchsia-50 border border-fuchsia-100 rounded-lg">
            <span className="font-medium text-gray-900">Orphan Guard:</span> Session &gt; 48 hours without activity = orphan (discarded).
            Session &lt; 48 hours without session.created = recovery attempt.
          </div>
          <div className="p-3 bg-fuchsia-50 border border-fuchsia-100 rounded-lg">
            <span className="font-medium text-gray-900">Process Guard:</span> Prevents multiple agent instances from running simultaneously.
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          16. ENROLLMENT SUMMARY DIALOG
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-lime-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11.25 11.25l.041-.02a.75.75 0 011.063.852l-.708 2.836a.75.75 0 001.063.853l.041-.021M21 12a9 9 0 11-18 0 9 9 0 0118 0zm-9-3.75h.008v.008H12V8.25z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">16. Enrollment Summary Dialog</h3>
        </div>
        <div className="p-4 bg-lime-50 border border-lime-200 rounded-lg text-sm text-gray-700 space-y-2">
          <p>Optional UI dialog shown at enrollment completion</p>
          <p>Configurable timeout (EnrollmentSummaryTimeoutSeconds)</p>
          <p>Custom branding image (BrandingImageUrl)</p>
          <p>Event: <span className="font-mono text-xs bg-lime-100 px-1 py-0.5 rounded">enrollment_summary_shown</span></p>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          17. CLEANUP & SELF-DESTRUCT
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-gray-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M14.74 9l-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 01-2.244 2.077H8.084a2.25 2.25 0 01-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 00-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 013.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 00-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 00-7.5 0" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">17. Cleanup & Self-Destruct</h3>
        </div>
        <div className="space-y-3 text-sm text-gray-700">
          <Step n={1} color="gray" title="Task Removal:">Deletes Scheduled Task via schtasks.exe /Delete</Step>
          <Step n={2} color="gray" title="File Cleanup:">Deletes all agent files (or keeps logs if KeepLogFile=true)</Step>
          <Step n={3} color="gray" title="Optional Reboot:">RebootOnComplete + configurable RebootDelaySeconds (default: 10)</Step>
          <Step n={4} color="gray" title="Async Execution:">PowerShell cleanup script to avoid locking the current process</Step>
        </div>
        <div className="mt-3 p-3 bg-gray-50 border border-gray-200 rounded-lg text-sm text-gray-700">
          <span className="font-medium">Ghost Restart Detection:</span> If Deployed registry key exists but no session.id file &mdash; stale deployment detected, agent runs cleanup retry.
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          18. SPECIAL EXECUTION MODES
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-pink-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 9l3 3-3 3m5 0h3M5 20h14a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">18. Special Execution Modes</h3>
        </div>

        <div className="space-y-4">
          <div className="border border-pink-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-pink-50 border-b border-pink-200">
              <p className="font-semibold text-sm text-pink-900">--run-gather-rules</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700">
              Executes startup gather rules once and exits. For isolated rule testing and diagnostics.
            </div>
          </div>
          <div className="border border-pink-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-pink-50 border-b border-pink-200">
              <p className="font-semibold text-sm text-pink-900">--run-ime-matching</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700">
              Parses IME logs offline and produces <span className="font-mono text-xs bg-pink-100 px-1 py-0.5 rounded">ime_pattern_matching.log</span>. For pattern debugging without real enrollment.
            </div>
          </div>
          <div className="border border-pink-200 rounded-lg overflow-hidden">
            <div className="px-4 py-2.5 bg-pink-50 border-b border-pink-200">
              <p className="font-semibold text-sm text-pink-900">Log Replay Mode</p>
            </div>
            <div className="px-4 py-3 text-sm text-gray-700">
              Replays IME logs with time compression (default: 50x). <span className="font-mono text-xs bg-pink-100 px-1 py-0.5 rounded">--replay-log-dir</span> +
              <span className="font-mono text-xs bg-pink-100 px-1 py-0.5 rounded">--replay-speed-factor</span>.
              Creates real sessions in the backend. Enables full regression testing without waiting for real-time enrollment.
            </div>
          </div>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          19. LOGGING
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-stone-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19.5 14.25v-2.625a3.375 3.375 0 00-3.375-3.375h-1.5A1.125 1.125 0 0113.5 7.125v-1.5a3.375 3.375 0 00-3.375-3.375H8.25m0 12.75h7.5m-7.5 3H12M10.5 2.25H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 00-9-9z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">19. Logging</h3>
        </div>
        <div className="p-4 bg-stone-50 border border-stone-200 rounded-lg text-sm text-gray-700 space-y-2">
          <p>File-based with daily rotation: <span className="font-mono text-xs bg-stone-100 px-1 py-0.5 rounded">agent_YYYYMMDD.log</span></p>
          <p>Levels: <span className="font-mono text-xs bg-stone-100 px-1 py-0.5 rounded">Info</span> (default), <span className="font-mono text-xs bg-stone-100 px-1 py-0.5 rounded">Debug</span>, <span className="font-mono text-xs bg-stone-100 px-1 py-0.5 rounded">Verbose</span>, <span className="font-mono text-xs bg-stone-100 px-1 py-0.5 rounded">Trace</span></p>
          <p>Configurable via CLI (<span className="font-mono text-xs bg-stone-100 px-1 py-0.5 rounded">--log-level</span>) and remote config</p>
          <p>Optional console mirror (<span className="font-mono text-xs bg-stone-100 px-1 py-0.5 rounded">--console</span> or interactive mode)</p>
          <p>Thread-safe writes, log injection prevention (newline/CR escaping)</p>
          <p>Crash logs: <span className="font-mono text-xs bg-stone-100 px-1 py-0.5 rounded">crash_*.log</span> with full stack trace</p>
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          20. COMPLETE EVENT TYPE REFERENCE
          ═══════════════════════════════════════════════════ */}
      <div className="mb-10">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-indigo-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.75 12h16.5m-16.5 3.75h16.5M3.75 19.5h16.5M5.625 4.5h12.75a1.875 1.875 0 010 3.75H5.625a1.875 1.875 0 010-3.75z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">20. Complete Event Type Reference</h3>
        </div>

        {/* Agent Lifecycle */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">Agent Lifecycle</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["agent_started", "MonitoringService", "Agent started (version, config, previous exit type)"],
              ["agent_shutdown", "MonitoringService", "Graceful shutdown"],
              ["agent_trace", "Various", "Decision tracing (verbose diagnostics)"],
              ["agent_version_check", "MonitoringService", "Version check outcome (up_to_date / updated / skipped / check_failed) - emitted every startup, session-scoped dedup for up_to_date"],
              ["system_reboot_detected", "MonitoringService", "Reboot detected between sessions"],
              ["security_audit", "MonitoringService", "Security flag status at startup"],
            ]}
          />
        </div>

        {/* Device Info */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">Device Information</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["os_info", "DeviceInfoCollector", "Windows version, build, edition"],
              ["boot_time", "DeviceInfoCollector", "System boot timestamp"],
              ["hardware_spec", "DeviceInfoCollector", "CPU, RAM, disk, manufacturer, model"],
              ["tpm_status", "DeviceInfoCollector", "TPM version, spec, ready status"],
              ["secureboot_status", "DeviceInfoCollector", "UEFI Secure Boot status"],
              ["bitlocker_status", "DeviceInfoCollector", "BitLocker encryption status"],
              ["aad_join_status", "DeviceInfoCollector", "Azure AD join state, user, tenant"],
              ["autopilot_profile", "DeviceInfoCollector", "Autopilot registration, group tag, mode"],
              ["enrollment_type_detected", "DeviceInfoCollector", "v1 Classic/ESP vs v2 WDP"],
              ["esp_config_detected", "DeviceInfoCollector", "ESP skip flags"],
              ["network_adapters", "DeviceInfoCollector", "All NICs"],
              ["dns_configuration", "DeviceInfoCollector", "DNS servers per NIC"],
              ["proxy_configuration", "DeviceInfoCollector", "WinHTTP proxy settings"],
              ["active_network_interface", "DeviceInfoCollector", "Active NIC details, WiFi SSID"],
            ]}
          />
        </div>

        {/* ESP & Enrollment */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">ESP & Enrollment</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["esp_phase_changed", "EnrollmentTracker", "Phase transition (DeviceSetup/AccountSetup)"],
              ["esp_resumed", "EnrollmentTracker", "ESP re-detected (hybrid join recovery)"],
              ["esp_provisioning_raw", "EspAndHelloTracker", "Registry provisioning status snapshot"],
              ["esp_provisioning_settle_started", "EnrollmentTracker", "Waiting for registry update"],
              ["esp_failure_detected", "EspAndHelloTracker", "ESP failure (with grace period)"],
              ["enrollment_complete", "EnrollmentTracker", "Enrollment succeeded"],
              ["enrollment_failed", "EnrollmentTracker", "Enrollment failed"],
              ["completion_check", "EnrollmentTracker", "Throttled completion state check"],
              ["enrollment_summary_shown", "MonitoringService", "Summary dialog shown"],
              ["waiting_for_hello", "EnrollmentTracker", "Waiting for WHfB provisioning"],
            ]}
          />
        </div>

        {/* Windows Hello */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">Windows Hello for Business</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["hello_policy_detected", "EspAndHelloTracker", "WHfB policy found in registry"],
              ["hello_provisioning_willlaunch", "EspAndHelloTracker", "Prerequisites passed (event 358)"],
              ["hello_provisioning_completed", "EspAndHelloTracker", "NGC key registered (event 300)"],
              ["hello_provisioning_failed", "EspAndHelloTracker", "NGC key failed (event 301)"],
              ["hello_provisioning_willnotlaunch", "EspAndHelloTracker", "Prerequisites failed (event 360)"],
              ["hello_provisioning_blocked", "EspAndHelloTracker", "Provisioning blocked (event 362)"],
              ["hello_pin_status", "EspAndHelloTracker", "PIN status"],
              ["hello_wait_timeout", "EspAndHelloTracker", "Wizard did not start in time"],
              ["hello_completion_timeout", "EspAndHelloTracker", "Wizard timed out"],
              ["hello_wizard_started", "EspAndHelloTracker", "Shell-Core event 62404"],
            ]}
          />
        </div>

        {/* App Installation */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">App Installation (IME)</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["app_download_started", "EnrollmentTracker", "App download phase began"],
              ["app_install_started", "EnrollmentTracker", "App installation phase began"],
              ["app_install_completed", "EnrollmentTracker", "App installed successfully"],
              ["app_install_failed", "EnrollmentTracker", "App installation failed"],
              ["app_install_skipped", "EnrollmentTracker", "App skipped"],
              ["app_install_postponed", "EnrollmentTracker", "App installation postponed"],
              ["download_progress", "EnrollmentTracker", "Periodic download/install progress"],
              ["app_tracking_summary", "EnrollmentTracker", "Periodic app state snapshot"],
              ["do_telemetry", "EnrollmentTracker", "DO metadata per app"],
            ]}
          />
        </div>

        {/* IME Process */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">IME Process</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["ime_agent_version", "EnrollmentTracker", "IME version from logs"],
              ["ime_process_exited", "ImeProcessWatcher", "IntuneManagementExtension.exe exit"],
              ["ime_session_change", "EnrollmentTracker", "IME user session change"],
              ["ime_user_session_completed", "EnrollmentTracker", "IME user session completed"],
            ]}
          />
        </div>

        {/* Performance & Metrics */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">Performance & Metrics</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["performance_snapshot", "PerformanceCollector", "System performance metrics"],
              ["performance_collector_stopped", "MonitoringService", "Collector stopped (idle)"],
              ["agent_metrics_snapshot", "AgentSelfMetricsCollector", "Agent self-metrics"],
              ["agent_metrics_collector_stopped", "MonitoringService", "Collector stopped (idle)"],
              ["do_status_snapshot", "DOCollector", "Delivery Optimization status"],
            ]}
          />
        </div>

        {/* Network, Detection, Security */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">Network, Detection & Security</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["network_state_change", "NetworkChangeDetector", "SSID/adapter/IP change"],
              ["network_connectivity_check", "NetworkChangeDetector", "MDM endpoint reachability"],
              ["desktop_arrived", "DesktopArrivalDetector", "Explorer.exe under real user"],
              ["configmgr_client_detected", "EnrollmentTracker", "ConfigMgr co-management detected"],
              ["local_admin_analysis", "LocalAdminAnalyzer", "Local admin audit"],
              ["software_inventory_analysis", "SoftwareInventoryAnalyzer", "Software inventory + delta"],
              ["security_warning", "GatherRuleCollectors", "Security finding from gather rule"],
            ]}
          />
        </div>

        {/* Geo, Time, WhiteGlove, Diagnostics, Cleanup */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">Geo, Time, WhiteGlove, Diagnostics & Cleanup</h4>
          <DataTable
            headers={["Event Type", "Source", "Description"]}
            rows={[
              ["device_location", "GeoLocationService", "IP geolocation result"],
              ["ntp_time_check", "NtpTimeCheckService", "NTP time offset"],
              ["timezone_auto_set", "TimezoneService", "Timezone set from geolocation"],
              ["whiteglove_complete", "EspAndHelloTracker", "WhiteGlove Part 1 complete"],
              ["whiteglove_resumed", "MonitoringService", "WhiteGlove Part 2 resume"],
              ["diagnostics_collecting", "MonitoringService", "Gathering diagnostics"],
              ["diagnostics_upload_attempted", "DiagnosticsPackageService", "Upload attempt with result"],
              ["reboot_triggered", "MonitoringService", "System reboot initiated"],
            ]}
          />
        </div>
      </div>

      {/* ═══════════════════════════════════════════════════
          21. API ENDPOINTS
          ═══════════════════════════════════════════════════ */}
      <div className="mb-4">
        <div className="flex items-center gap-2 mb-4">
          <svg className="w-6 h-6 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5.25 14.25h13.5m-13.5 0a3 3 0 01-3-3m3 3a3 3 0 100 6h13.5a3 3 0 100-6m-16.5-3a3 3 0 013-3h13.5a3 3 0 013 3m-19.5 0a4.5 4.5 0 01.9-2.7L5.737 5.1a3.375 3.375 0 012.7-1.35h7.126c1.062 0 2.062.5 2.7 1.35l2.587 3.45a4.5 4.5 0 01.9 2.7m0 0a3 3 0 01-3 3m0 3h.008v.008h-.008v-.008zm0-6h.008v.008h-.008v-.008zm-3 6h.008v.008h-.008v-.008zm0-6h.008v.008h-.008v-.008z" />
          </svg>
          <h3 className="text-xl font-bold text-gray-900">21. API Endpoints (Agent &rarr; Backend)</h3>
        </div>

        <div className="mb-4">
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">Authenticated (mTLS)</h4>
          <DataTable
            headers={["Endpoint", "Method", "Purpose"]}
            rows={[
              ["/api/agent/config", "GET", "Fetch agent configuration"],
              ["/api/agent/register-session", "POST", "Register new enrollment session"],
              ["/api/agent/ingest", "POST", "Upload batched events (NDJSON + gzip)"],
              ["/api/agent/error", "POST", "Emergency error report"],
              ["/api/agent/distress", "POST", "Pre-auth distress signal (no cert needed)"],
              ["/api/diagnostics/upload-url", "POST", "Get short-lived SAS URL for diagnostics"],
            ]}
          />
        </div>

        <div>
          <h4 className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-2">Bootstrap (Pre-Enrollment)</h4>
          <DataTable
            headers={["Endpoint", "Method", "Purpose"]}
            rows={[
              ["/api/bootstrap/validate/{code}", "GET", "Validate bootstrap code, get token"],
              ["/api/bootstrap/register-session", "POST", "Register session with bootstrap token"],
              ["/api/bootstrap/ingest", "POST", "Upload events with bootstrap token"],
              ["/api/bootstrap/config", "GET", "Get config with bootstrap token"],
              ["/api/bootstrap/error", "POST", "Report errors with bootstrap token"],
            ]}
          />
        </div>
      </div>

    </section>
  );
}
