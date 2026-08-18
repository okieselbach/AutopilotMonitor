/**
 * Static "landkarte" of an agent diagnostics ZIP archive.
 *
 * Returned by `get_session_diagnostics` and the `get_resource(name="diag_zip_layout")`
 * catalog so an AI client knows what to expect inside the ZIP it downloads locally —
 * without the backend or MCP server ever unzipping/parsing the archive (that would burn
 * server CPU on multi-hundred-MB IME logs). The client downloads the raw ZIP, extracts
 * it locally, and uses this map to decide what to read first and how (grep vs full read).
 *
 * This is a fixed convention map, NOT a per-archive manifest — it costs nothing to produce
 * (no ZIP read). The actual file set varies: V1 agents omit AgentState/AgentSpool; upload
 * mode / size caps may drop files; a `_TRUNCATED.txt` marks a size-capped archive.
 */
export const DIAG_ZIP_MAP = {
  description:
    'Expected layout of an Autopilot-Monitor agent diagnostics ZIP. Download the ZIP from the ' +
    'tool\'s downloadUrl, extract it locally, then read files in the priority order below. ' +
    'Correlate findings with backend telemetry via get_session_events / query_raw_events / ' +
    'search_knowledge. The agent log is the source of truth for the CLIENT side; the Events ' +
    'table is what the backend actually received — gaps between them reveal upload/network issues.',
  agentLogFormat: '[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] message — LEVEL ∈ INFO|WARN|ERROR|DEBUG|VERBOSE|TRACE',
  files: [
    {
      path: 'AgentState/final-status.json',
      what: 'Completion outcome: outcome, completionSource, helloOutcome, signalsSeen[], signalTimestamps{}, app summary.',
      read: 'full',
      priority: 1,
      appliesTo: 'v2',
    },
    {
      path: 'AgentLogs/agent.log',
      what: 'Full client chronology: startup → config → collectors → enrollment tracking → completion → upload. Size-rotated segments agent-002.log onward. Pre-2026-08-18 agents wrote date-rotated agent_YYYYMMDD.log instead — glob agent*.log to catch both.',
      read: 'grep-errors-first',
      grepHints: ['"\\[WARN\\]\\|\\[ERROR\\]"', '-A 5 -B 2 "\\[ERROR\\]"', '"DecisionStep: ord="', '"EventUploadService"', '"EnrollmentTracker"', '"DiagnosticsCollector"'],
      priority: 2,
      appliesTo: 'v1+v2',
    },
    {
      path: 'AgentState/journal.jsonl',
      what: 'DecisionCore journal — DecisionStep records with ordinals + stage transitions (SessionStarted→EspDeviceSetup→…→Completed).',
      read: 'full-or-grep',
      priority: 3,
      appliesTo: 'v2',
    },
    {
      path: 'AgentState/signal-log.jsonl',
      what: 'Every signal ingested by the decision engine (the inputs that drove completion).',
      read: 'full',
      priority: 4,
      appliesTo: 'v2',
    },
    {
      path: 'AgentSpool/spool.jsonl + upload-cursor.json',
      what: 'Telemetry spool + last-uploaded cursor. upload-cursor.LastUploadedItemId == last spool TelemetryItemId ⇒ no event lost.',
      read: 'full',
      priority: 5,
      appliesTo: 'v2',
    },
    {
      path: 'AgentLogs/ime-pattern-matches.log (older v2 agents: ime_pattern_matches.log; v1: ime-matches.log)',
      what: 'IME pattern matches. Format: [sourceFileName] [patternId] rawLogLine. Read when the question is about apps / ESP phases / IME patterns.',
      read: 'full',
      priority: 6,
      appliesTo: 'v1+v2',
    },
    {
      path: 'ImeLogs/AppWorkload*.log',
      what: 'App-install enforcement/execution/detection detail from IME.',
      read: 'grep-only',
      warning: 'CAN BE HUNDREDS OF MB — never read whole; always grep (e.g. app GUID, "EnforcementState", "EspPhase").',
      priority: 7,
      appliesTo: 'v1+v2',
    },
    {
      path: 'ImeLogs/IntuneManagementExtension.log',
      what: 'IME main log.',
      read: 'grep',
      priority: 8,
      appliesTo: 'v1+v2',
    },
    {
      path: 'AgentState/startup-event-state.json / office-install-state.json / ime-tracker-state.json',
      what: 'Persisted feature state across reboots (StartupEventGate fingerprints, Office C2R lifecycle, IME tracker).',
      read: 'full-as-needed',
      priority: 9,
      appliesTo: 'v2',
    },
    {
      path: 'AgentLogs/do-status.jsonl',
      what: 'Delivery-Optimization polls (raw).',
      read: 'as-needed',
      priority: 10,
      appliesTo: 'v2',
    },
    {
      path: 'sessioninfo.txt',
      what: 'Client-side session metadata (SessionId, TenantId, device, OS, agent version) — quick sanity cross-check vs backend.',
      read: 'full',
      priority: 11,
      appliesTo: 'v1+v2',
    },
    {
      path: 'AdditionalLogs/',
      what: 'Tenant-configured extra log paths (varies per tenant).',
      read: 'as-needed',
      priority: 12,
      appliesTo: 'v1+v2',
    },
  ],
  notes: [
    'enrollment-complete.marker and session.id are absent by construction (marker written AFTER upload; session.id is config).',
    'A _TRUNCATED.txt entry means the archive hit its size cap — some content is missing.',
    'V1 agent archives have NO AgentState/ or AgentSpool/ folders (DecisionCore is v2-only).',
    'Pre-provisioned (White Glove) blobs carry a -preprov suffix — that is normal.',
    'No archive ≠ a problem: upload mode may be Off or OnFailure (so successful sessions have none).',
    'Log naming is kebab-case since 2026-08-18: agent.log, bootstrap-script.log (was bootstrap_agent.log), bootstrap-msi.log (was msi-bootstrap.log), crash-*.log (was crash_*.log), gather-rules-debug.log, ime-pattern-matches.log. Archives from older agents/bootstraps carry the old names.',
  ],
} as const;
