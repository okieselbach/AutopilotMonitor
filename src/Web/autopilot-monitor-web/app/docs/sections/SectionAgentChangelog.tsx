export function SectionAgentChangelog() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Agent Changelog</h2>
      </div>
      <p className="text-gray-600 mb-8">
        User-facing changes to the Autopilot Monitor agent, newest first. Only includes changes that affect agent behavior on the device.
      </p>

      {/* ── June 2026 ────────────────────────────────── */}
      <ChangelogBlock title="June 2026">
        <Li>Optional keep-awake during the User-ESP (Account Setup) phase — when enabled per tenant (off by default), the agent keeps the device awake so it can&apos;t drop into standby and stall app installs or account setup; the hold is released automatically once the phase completes, and device reboots are unaffected</Li>
        <Li>Fewer stuck enrollments — Classic sessions where a skipped user-ESP app or an advisory &quot;continue anyway&quot; ESP failure left completion waiting now resolve through a short completion deadline instead of running into the 6-hour timeout</Li>
        <Li>Desktop-arrival detection no longer stalls on devices where the owner lookup failed — the agent resolves the signed-in user via a Windows session (WTS) query, with the previous method as fallback</Li>
        <Li>When an app still installing blocks enrollment completion, the agent now names the specific app holding up the AccountSetup gate instead of just reporting a stall</Li>
        <Li>Office (M365 Apps) install tracking is more accurate — pre-installed OEM / consumer Office no longer triggers a false &quot;install failed&quot;, and Office install progress now survives agent restarts and reboots</Li>
        <Li>Low-disk-space warning — agent emits a one-shot warning when free space drops below 2&nbsp;GB during enrollment (re-arms once space recovers above 3&nbsp;GB)</Li>
        <Li>Repetitive ModernDeployment event bursts are rolled up into a single entry and excluded from the idle timer — less timeline noise, and they no longer keep the collector awake</Li>
        <Li>Hello for Business policy is no longer reported as <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">not_configured</code> when it simply couldn&apos;t be detected, avoiding misleading Hello status</Li>
        <Li>New liveness signals show what enrollment completion is waiting on, and flag a session that is parked without a deadline — making stuck sessions easier to spot</Li>
        <Li>Startup events (e.g. timezone, NTP, geo-location) are de-duplicated across agent restarts, so a reboot mid-enrollment no longer repeats them in the timeline</Li>
        <Li>RealmJoin client detection is now an opt-in per-tenant setting (off by default) and additionally reports the RealmJoin release channel alongside its version</Li>
        <Li>Agent records the device&apos;s outbound (egress) IP for network correlation during enrollment</Li>
        <Li>Each agent request now carries a correlation ID for end-to-end tracing across agent and backend, making request-level troubleshooting easier</Li>
        <Li>Microsoft 365 Apps (Office) install tracking — agent surfaces the real Office Click-to-Run install that the Intune &quot;integrated&quot; app hides (IME reports done within a minute or two while Office keeps streaming in the background); shown as its own install row with live Delivery Optimization download progress</Li>
        <Li>Provisioning-package detection — agent scans for Windows provisioning packages (<code className="text-xs bg-gray-100 px-1 py-0.5 rounded">.ppkg</code>) applied to the device and reports them in a single scan event; security rules flag packages outside a built-in allow-list of Windows-inbox packages</Li>
        <Li>AutoLogon detection — agent reports the device&apos;s automatic sign-in (AutoLogon) configuration; security rules now flag it only when a plaintext password is actually stored, so normal Autopilot enrollments no longer produce false positives</Li>
        <Li>ESP sub-category state changes are now surfaced even when they aren&apos;t failures (e.g. retry or recovery transitions) for clearer visibility into ESP progress</Li>
        <Li>Stall-probe file and registry scans now enforce a hard timeout so a slow or locked source can no longer hang the probe</Li>
      </ChangelogBlock>

      {/* ── May 2026 ─────────────────────────────────── */}
      <ChangelogBlock title="May 2026">
        <Li>RealmJoin client detection — agent detects the RealmJoin client, reports its version, tracks deployment-phase changes, and surfaces per-package install progress</Li>
        <Li>Device hardware now reports CPU architecture (<code className="text-xs bg-gray-100 px-1 py-0.5 rounded">x86</code> / <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">x64</code> / <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">ARM</code> / <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">ARM64</code>)</Li>
        <Li>Startup power-state check — agent warns when the device is running on battery below 80%, a frequent driver behind power-management enrollment stalls (enrollments are more reliable on AC power)</Li>
        <Li>ESP app-install failures are now classified by their HRESULT, surface all failure types (failed / not-installed / error), and a 30-second settle window catches results that arrive late</Li>
        <Li>Crash mini-dumps are captured for deeper post-mortem analysis when a previous-run crash is detected</Li>
        <Li>More accurate Health Script results — compliant detections are no longer mislabeled as failed, and detection failures now produce an actionable message for admins</Li>
        <Li>Fewer false enrollment failures — ESP &quot;continue anyway&quot; with AccountSetup, and self-deploying scenarios, now get a settle window before a failure is finalized</Li>
        <Li>Shutdown, diagnostics, and summary events are no longer dropped after a terminal enrollment decision</Li>
        <Li>Bootstrap reports its version so the bootstrap script version is visible in the session</Li>
        <Li>Agent retries the remote-config fetch with wire-visible fallback when the first attempt fails</Li>
        <Li>Security hardening for agent self-update, diagnostics URL handling, and PowerShell argument escaping</Li>
        <Li><strong>Agent V2 is now the primary production line</strong> — V2 replaces V1 as the default install. Existing V1 devices keep working; new installs ship the V2 build (bootstrap script and binary renamed from <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">.V2</code> to standard)</Li>
        <Li>Health Scripts lifecycle monitoring — detection, remediation, and post-remediation phases are each captured as separate timeline events with a live &quot;script running&quot; indicator before the result lands</Li>
        <Li>Apps still installing when ESP-Apps times out are flagged as &quot;likely stuck&quot; instead of disappearing from the timeline — admins now see the app name and a hedged outcome</Li>
        <Li>ASR / EDR-blocked install handoff no longer strands devices — runtime spawn fails soft and the BootTrigger task picks the agent back up on next reboot</Li>
        <Li>Hello-disabled enrollments now complete reliably — the Classic v1 path no longer deadlocks waiting for a Hello signal that will never arrive (previously ran into the 6h max-lifetime timer)</Li>
        <Li>AccountSetup must truly succeed before Hello can trigger completion — prevents premature <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">enrollment_complete</code> when AccountSetup actually failed</Li>
        <Li>Hybrid User-Driven (HAADJ) enrollment-completion gaps closed — more completion paths recognized, fewer sessions stuck in the timeout fallback</Li>
        <Li>TPM PSS unsupported is reported as a distinct distress reason — older devices (e.g. Surface Book 1 with 2015-era Infineon TPM firmware) that can&apos;t do RSA-PSS now get a clear failure category instead of a generic Schannel error</Li>
        <Li>Intune dual-stack certificate selection fix — on devices with both MDM and MMP-C client certs, the agent now picks the correct <em>Microsoft Intune MDM Device CA</em> cert and avoids backend chain-validation rejection</Li>
        <Li>Client certificate rejections surface with structured backend warnings and V2 distress cert-context (thumbprint, subject, issuer, validity) — easier to diagnose mTLS auth failures</Li>
        <Li>Tenant ID resolution falls back to the CloudDomainJoin registry (<code className="text-xs bg-gray-100 px-1 py-0.5 rounded">TenantInfo</code> + <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">JoinInfo</code>) when the Enrollments key is empty — covers pre-Type-6 enrollments and MS-Organization-Access cert paths</Li>
        <Li>Event-driven Tenant ID wait via RegistryWatcher — agent reacts to registry changes during pre-enrollment instead of polling</Li>
        <Li>Desktop Arrival Detector liveness signals (started / first-poll / no-candidate) help distinguish &quot;agent dead post-reboot&quot; from &quot;user never logged in&quot; in sessions that time out without a desktop_arrived</Li>
        <Li>Detailed shutdown reasons — when the agent exits unexpectedly (Ctrl+C, process exit, unhandled exception, runtime host exit) the cause is recorded in the timeline</Li>
        <Li>Prior-run crash is surfaced in the next session via a &quot;death rattle&quot; event, so a mid-enrollment agent crash is visible instead of silently lost</Li>
        <Li>V2 diagnostics ZIP is size- and count-capped with streaming output — no more multi-gigabyte uploads on long or noisy sessions</Li>
        <Li>Diagnostics ZIP now includes the <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">State</code> and <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">Spool</code> folders for richer post-mortem analysis</Li>
        <Li>Agent log files rotate at a size cap — no unbounded growth on long-running devices</Li>
        <Li>New &quot;Submit Logs&quot; page — admins can upload diagnostics files for analysis even when no active session exists on the device</Li>
        <Li>Delivery Optimization breakdown adds MCC (Microsoft Connected Cache) and LinkLocal sources across <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">download_progress</code> and <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">do_telemetry</code> events</Li>
        <Li>Software inventory now correctly enumerates Azure AD and personal MSA user profiles (these SIDs were previously skipped)</Li>
        <Li>Hardware spec event reports VM detection — security analyze rules skip VMs to avoid false-positive vulnerability reports</Li>
        <Li>Bootstrap <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">--install</code> mode preserves an existing <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">bootstrap-config.json</code> instead of clobbering customer settings on re-install</Li>
        <Li>Optional &quot;enrollment started&quot; webhook fires at session registration — opt-in notification at the very start of an enrollment</Li>
      </ChangelogBlock>

      {/* ── April 2026 ───────────────────────────────── */}
      <ChangelogBlock title="April 2026">
        <Li>Delivery Optimization monitoring — agent tracks Windows DO download activity (OS level) during app installs and reports download performance metrics per application</Li>
        <Li>ConfigMgr co-management detection — agent detects Configuration Manager client presence and reports co-management status with confidence scoring</Li>
        <Li>Non-whitelisted hardware detection with optional admin alerts when devices with unapproved hardware models enroll</Li>
        <Li>IME version change tracking — Intune Management Extension version updates are recorded</Li>
        <Li>Hello for Business skip detection — agent now distinguishes between Hello setup being completed, timed out, or explicitly skipped</Li>
        <Li>User-profile-aware diagnostics — gather rules and diagnostics log paths can reference the logged-on user profile directory</Li>
        <Li>Improved vulnerability matching accuracy using fuzzy Jaro-Winkler scoring</Li>
        <Li>Faster agent startup through optimized initialization flow</Li>
        <Li>ESP provisioning status verification before enrollment completion — agent checks category outcomes and waits up to 30s for pending results to settle</Li>
        <Li>Structured error codes (exit codes, HRESULT) extracted from IME log patterns and included in timeline events</Li>
        <Li>Dual-hash integrity verification — ZIP package hash checked at download, separate EXE hash verified at runtime against backend to detect post-installation tampering</Li>
        <Li>Vulnerability matching improvements — confidence levels, platform-aware filtering, and exclude patterns for more accurate reports</Li>
        <Li>Vulnerability reports now available during pre-provisioning (White Glove) sessions</Li>
        <Li>More reliable enrollment summary dialog launch with desktop fallback strategy</Li>
        <Li>PowerShell script output is now fully captured in the timeline (multi-line output was previously truncated)</Li>
        <Li>More reliable bootstrap and download handling with improved timeout and rate-limit behavior</Li>
        <Li>Agent reports self-update events so updates are visible in the session timeline</Li>
        <Li>Emergency channel — agent can send distress signals when it detects critical failures</Li>
        <Li>ESP &quot;resumed&quot; event is now only emitted for Hybrid Join scenarios (avoids noise on other paths)</Li>
        <Li>Improved crash recovery — completion state is persisted so the agent can resume correctly after an unexpected restart</Li>
      </ChangelogBlock>

      {/* ── Late March 2026 ──────────────────────────── */}
      <ChangelogBlock title="Late March 2026">
        <Li>Agent crash detection — crashes are automatically detected and reported to the backend</Li>
        <Li>SHA-256 integrity verification for agent downloads (bootstrapper + self-updater verify hash before install)</Li>
        <Li>Reboot tracking — reboots during enrollment are now tracked and visible in the timeline</Li>
        <Li>NTP time sync check with clock skew warning when device time is significantly off</Li>
        <Li>Automatic timezone detection and configuration</Li>
        <Li>SecureBoot certificate collection for security posture reporting</Li>
        <Li>IME process watcher — detects when the Intune Management Extension starts or stops</Li>
        <Li>Network change detection — captures network adapter changes during enrollment</Li>
        <Li>Agent self-update mechanism — outdated agents in the field update themselves automatically</Li>
        <Li>Unrestricted mode option (per-tenant) to disable most guard rails</Li>
        <Li>Notification system reworked — supports Teams (legacy + Workflow), Slack, and custom webhooks</Li>
      </ChangelogBlock>

      {/* ── Mid March 2026 ───────────────────────────── */}
      <ChangelogBlock title="Mid March 2026">
        <Li>Software inventory collection with automatic vulnerability correlation (CVE matching)</Li>
        <Li>Hardware specification event — detailed hardware info collected and reported</Li>
        <Li>Agent shutdown event — clean shutdown is now explicitly tracked</Li>
        <Li>Postponed app detection and handling during enrollment</Li>
        <Li>Self-deploying mode detection and event tracking</Li>
        <Li>Enrollment summary dialog shown on the device after enrollment completes</Li>
        <Li>ESP provisioning status tracking — catches non-IME errors like certificate failures</Li>
        <Li>PowerShell script execution tracking during enrollment</Li>
        <Li>Clock skew detection with geo-location failure reporting</Li>
        <Li>Community analyze rules support</Li>
      </ChangelogBlock>

      {/* ── Early March 2026 ─────────────────────────── */}
      <ChangelogBlock title="Early March 2026">
        <Li>Bootstrap session support — monitoring starts before MDM enrollment (during OOBE)</Li>
        <Li>ESP configuration detection — identifies ESP settings on the device</Li>
        <Li>TPM info collection for device details</Li>
        <Li>Activity-aware idle timeout replaces fixed 4-hour collector limit (default: 15 min idle)</Li>
        <Li>Reliable session end-detection for all deployment scenarios (user-driven, pre-provisioning, hybrid)</Li>
        <Li>Network performance data collection (latency, throughput)</Li>
        <Li>Geographic location support via IP-based lookup</Li>
        <Li>Emergency break — remote kill switch to stop agents</Li>
        <Li>Automatic retry on transient backend errors</Li>
        <Li>Custom User-Agent header for easier firewall allowlisting</Li>
        <Li>ESP state tracking via registry watcher</Li>
        <Li>XML and JSON file gathering in diagnostics</Li>
        <Li>Configurable <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">--await-enrollment</code> parameter for pre-enrollment wait</Li>
      </ChangelogBlock>

      {/* ── Late February 2026 ───────────────────────── */}
      <ChangelogBlock title="Late February 2026">
        <Li>Pre-Provisioning (White Glove) support — full end-to-end monitoring of pre-provisioning sessions</Li>
        <Li>mTLS for all agent-to-backend communication (consolidated endpoints)</Li>
        <Li>Diagnostics SAS URL fetched on-demand — no longer stored on disk</Li>
        <Li>Max collector duration policy (configurable per tenant)</Li>
        <Li>Diagnostics package upload from device</Li>
        <Li>Configurable reboot-on-complete and keep-logfile options via remote config</Li>
        <Li>Configurable diagnostics log paths (global + per-tenant)</Li>
        <Li>Lenovo model detection fix (WMI query)</Li>
      </ChangelogBlock>

      {/* ── Mid February 2026 ────────────────────────── */}
      <ChangelogBlock title="Mid February 2026">
        <Li>Windows Autopilot v2 (Device Preparation) support</Li>
        <Li>GatherRules guard rails — prevents collection of overly broad paths</Li>
        <Li>IME log replay for testing and demos (<code className="text-xs bg-gray-100 px-1 py-0.5 rounded">--replay-log-dir</code>)</Li>
        <Li>Agent state persistence — survives reboots and resumes monitoring</Li>
        <Li>Embedded Intune root + intermediate certificates for chain validation</Li>
        <Li>OS info and boot time collection</Li>
        <Li>Hello screen detection improvements</Li>
        <Li>Download progress tracking</Li>
      </ChangelogBlock>

      {/* ── Early February 2026 ──────────────────────── */}
      <ChangelogBlock title="Early February 2026">
        <Li>Initial agent release</Li>
        <Li>Real-time enrollment telemetry (IME log parsing, ESP phases, app installs)</Li>
        <Li>Geolocation support for enrollment sessions</Li>
        <Li>Hello screen detector for enrollment completion</Li>
        <Li>Reboot-on-complete support</Li>
        <Li>Session ID persistence across agent restarts</Li>
        <Li>Bootstrap token authentication for pre-MDM scenarios</Li>
      </ChangelogBlock>
    </section>
  );
}

/* ── Helpers ──────────────────────────────────────────── */

function ChangelogBlock({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="mb-6">
      <h3 className="text-lg font-semibold text-gray-900 mb-2 border-b border-gray-200 pb-1">{title}</h3>
      <ul className="space-y-1.5 text-sm text-gray-700 list-disc list-inside">{children}</ul>
    </div>
  );
}

function Li({ children }: { children: React.ReactNode }) {
  return <li>{children}</li>;
}
