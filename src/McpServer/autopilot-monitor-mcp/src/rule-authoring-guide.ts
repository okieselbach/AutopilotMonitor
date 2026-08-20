/**
 * The rule-authoring guide served via get_resource(name="rule_authoring_guide").
 *
 * This is the complete, deterministic knowledge an AI needs to author gather
 * and analyze rules — the counterpart to retrieval via search_docs, which only
 * returns fragments. Everything here mirrors verified engine behaviour:
 * - condition/factor semantics: Backend RuleEngine.ConditionEvaluators.cs
 * - guardrail matching: Agent GatherRuleGuards.cs
 * - schema contract: rules/schema/*.schema.json (served as rule_schemas,
 *   generated into rule-authoring.generated.ts — the drift test pins the two).
 *
 * Keep enum lists in sync with the schemas — __tests__/rule-authoring-drift.test.ts
 * fails the build when they diverge.
 */

export const RULE_AUTHORING_GUIDE = {
  overview:
    'Autopilot Monitor has two customer-authorable rule families:\n' +
    '- GATHER rules run ON the device during enrollment and collect extra data ' +
    '(registry values, event log entries, WMI queries, files, allow-listed commands, parsed logs). ' +
    'Each execution emits an event with the rule\'s outputEventType.\n' +
    '- ANALYZE rules run in the backend AFTER enrollment (and on-demand) against the session\'s ' +
    'event stream and produce findings with explanation, remediation and a confidence score.\n' +
    'They compose: a gather rule collects a signal, an analyze rule turns it into a finding.',

  authoringWorkflow: [
    '1. Read this guide plus get_resource(name="rule_schemas") (the JSON Schema contract), ' +
      'get_resource(name="rule_guardrails") (what gather rules may touch) and ' +
      'get_resource(name="event_types") (which event types exist).',
    '2. Draft the rule as JSON. If the analysis needs data no existing event carries, draft the ' +
      'gather rule FIRST, then reference its outputEventType in the analyze rule.',
    '3. Call validate_rule with the draft — fix every error, take warnings seriously.',
    '4. For analyze rules: call test_analyze_rule with the draft plus a sessionId of a real ' +
      'session from your tenant (ideally one where the rule SHOULD fire and one where it should ' +
      'NOT). The dry-run returns a per-condition trace with evidence — iterate until the verdict ' +
      'matches your expectation. Nothing is persisted by a dry-run.',
    '5. Gather rules cannot be dry-run against a session (they execute on devices). For logparser ' +
      'rules, test the regex with test_log_pattern against sample lines pasted from the real log ' +
      'file — it uses the agent\'s exact .NET matching semantics. Then deploy to a test device/VM; ' +
      'the agent debug log setting for gather rules shows per-rule execution detail on-device.',
    '6. Create the finished rule in the portal (Settings → Gather Rules / Analyze Rules). Custom ' +
      'rules are tenant-scoped. Well-tested rules can be proposed as community rules via GitHub.',
  ],

  ids: {
    gather: 'Tenant custom gather rules: GATHER-CUSTOM-{NNN} (e.g. GATHER-CUSTOM-011), matching ^GATHER-[A-Z]+-\\d{3}$.',
    analyze: 'Tenant custom analyze rules: ANALYZE-CUSTOM-{NNN} (e.g. ANALYZE-CUSTOM-101), matching ^ANALYZE-[A-Z]+-\\d{3}$.',
    note:
      'The numeric namespace (ANALYZE|GATHER)-<CATEGORY>-<NUMBER> outside the CUSTOM category is ' +
      'RESERVED for rules shipped with the platform — the backend rejects tenant custom rules with ' +
      'such IDs (HTTP 409), including currently unused numbers: gaps are usually retired built-ins ' +
      'that may return. Use the CUSTOM category: it is the one scheme that passes both the schema ' +
      'pattern and the reservation. (The portal also accepts free-form IDs like an organization ' +
      'prefix, but those fail the strict schema check in validate_rule — prefer CUSTOM.)',
  },

  gatherRules: {
    collectorTypes: {
      registry:
        'Read a registry value or enumerate subkeys. target = key path WITHOUT the HKLM\\ prefix ' +
        'semantics of your choice — use the exact prefixes from rule_guardrails (they are rooted at ' +
        'HKLM). parameters: valueName (single value), listSubkeys ("true"), severityIfExists / ' +
        'severityIfNotExists to escalate the emitted event severity.',
      eventlog:
        'Query a Windows event log channel. target = channel name (must be in the guardrails ' +
        'eventLogChannels allowlist; Security/PowerShell/Sysmon channels are hard-blocked). ' +
        'parameters: eventId, source, maxEntries.',
      wmi: 'Run a WMI query. target = the query; it must start with one of the allow-listed prefixes ' +
        '(e.g. "SELECT * FROM Win32_BIOS").',
      file:
        'Check file/directory existence, optionally read content. target = path under an allow-listed ' +
        'prefix. parameters: readContent ("true"). C:\\Users is blocked.',
      json:
        'Query a JSON file with a JSONPath expression. target = file path under an allow-listed ' +
        'prefix (same file guardrails as collectorType "file", 200 KB cap). parameters: jsonpath ' +
        '(REQUIRED, e.g. "$.status.lastSync"), maxResults (default 20, max 100).',
      xml:
        'Query an XML file with an XPath expression. target = file path under an allow-listed ' +
        'prefix (same file guardrails as collectorType "file", 200 KB cap). parameters: xpath ' +
        '(REQUIRED), namespaces (optional "prefix=uri;prefix2=uri2"), maxResults (default 20, max 100).',
      command_allowlisted:
        'Run one of the exactly allow-listed commands (see rule_guardrails allowedCommands). ' +
        'Matching is EXACT (trimmed, case-insensitive) — no arguments can be added or removed.',
      logparser:
        'Parse log files with a regex. target = file/glob under an allow-listed prefix. parameters: ' +
        'pattern (regex, named groups become event data fields on the emitted timeline event), ' +
        'format ("cmtrace" = default: each line is parsed as CMTrace/IME format first and the regex ' +
        'runs against the parsed MESSAGE only; "text" = regex against the raw line), maxLines, ' +
        'trackPosition ("true" = only new lines on re-runs). Matching semantics: .NET regex engine, ' +
        'case-SENSITIVE (unlike analyze-rule regex conditions), first match per line. ALWAYS verify ' +
        'the pattern with test_log_pattern against real lines from the log file — engines like ' +
        'JS/PHP/Python behave subtly differently from the agent\'s .NET engine.',
    },
    triggers: {
      startup: 'Once when the agent starts.',
      phase_change:
        'When the enrollment reaches a phase (set triggerPhase). Phases follow the engine-reduced ' +
        'timeline — the same phases the portal timeline shows, not raw ESP registry flips.',
      phase_exit: 'When the enrollment leaves a phase (set triggerPhase).',
      interval: 'Periodically (set intervalSeconds).',
      on_event:
        'When a specific event type is emitted (set triggerEventType). This sees every emitted ' +
        'event including engine events such as enrollment_complete.',
    },
    phases: ['Start', 'DevicePreparation', 'DeviceSetup', 'AppsDevice', 'AccountSetup', 'AppsUser', 'FinalizingSetup', 'Complete'],
    scoping:
      'Optional activePhases (array) or activeFromPhase restrict when the rule may run at all; ' +
      'emitMode "on_change" emits only when the collected payload differs from the previous run ' +
      '(default "always").',
    output:
      'outputEventType names the emitted event; use the gather_ prefix convention (e.g. ' +
      '"gather_pending_reboot") so analyze rules and humans can tell collected events from ' +
      'built-in agent events. outputSeverity: Info | Warning | Error | Critical.',
    guardrails:
      'Every target is checked on-device against rules/guardrails.json (served as ' +
      'rule_guardrails). Matching semantics: registry/file prefixes are segment-bounded (the next ' +
      'character after the prefix must be "\\"), WMI prefixes bound on whitespace, commands match ' +
      'exactly, event log channels bound on "/". Hard blocks (Security event log, PowerShell ' +
      'channels, C:\\Users, download/exec-style commands) are enforced in agent code and cannot be ' +
      'lifted by config. If a needed target is not allow-listed, the allowlist itself has to be ' +
      'extended via a GitHub contribution to rules/guardrails.json — a rule alone cannot widen it.',
    template: {
      ruleId: 'GATHER-CUSTOM-101',
      title: 'Collect pending file rename operations',
      description: 'Reads PendingFileRenameOperations to detect a pending reboot blocking installs.',
      category: 'device',
      version: '1.0.0',
      author: 'Contoso IT',
      enabled: true,
      collectorType: 'registry',
      target: 'SYSTEM\\CurrentControlSet\\Control\\Session Manager',
      parameters: { valueName: 'PendingFileRenameOperations' },
      trigger: 'phase_change',
      triggerPhase: 'DeviceSetup',
      outputEventType: 'gather_pending_file_renames',
      outputSeverity: 'Info',
      tags: ['device', 'reboot'],
    },
  },

  analyzeRules: {
    conditionSources: {
      event_type:
        'Does an event of eventType exist? With dataField+operator+value it additionally filters ' +
        'on a field inside those events. Most common source.',
      event_data:
        'Compare a data field inside events of eventType (dataField + operator + value). Supports ' +
        'dot-paths into nested objects; "message" resolves to the event message.',
      event_data_array:
        'Iterate an ARRAY field (dataField) inside ONE event and test a sub-field of each element ' +
        '(itemField; empty = element itself). Matches if ANY element passes. Allow-list pattern: ' +
        'operator not_regex against an ANCHORED allow-regex (start with ^ and use \\b boundaries) ' +
        'fires only for elements NOT on the list — an unanchored regex would let ' +
        '"Evil.Prefix.AllowedName.Suffix" impostors through.',
      event_count:
        'Count events of eventType: operator count_gte (global) or count_per_group_gte (per ' +
        'distinct dataField value, e.g. 3+ failures of the SAME app via dataField=appId). Optional ' +
        'filterField/filterOperator/filterValue restrict which events are counted.',
      phase_duration:
        'Match a phase (eventType=esp_phase_changed, dataField=espPhase, operator=equals, ' +
        'value=<phase>); the evidence carries durationSeconds. Combine with a confidenceFactor ' +
        '"phase_duration > N" for the actual duration check.',
      app_install_duration: 'Compare app install duration in seconds (operator gt/lt/gte/lte, value=seconds).',
      event_correlation:
        'Join two event types over a shared field: eventType + correlateEventType + joinField, ' +
        'optional timeWindowSeconds, optional eventAFilterField/-Operator/-Value to pre-filter the ' +
        'first event, optional dataField+operator+value tested on the correlated pair.',
      clock_skew:
        'Device-clock check against the server receive frame: skewMetric=clock_jump (persistent ' +
        'mid-session step) or sustained_offset (whole session off by >= value). value = threshold ' +
        'seconds, operator gt/gte on the magnitude. No eventType/dataField; IME-log-derived events ' +
        'are excluded from the measurement. Batches from agents that send X-Send-Time-Utc are ' +
        'measured directly from the send time (spool-immune; evidence sentAtBatchCount counts them), ' +
        'older agents fall back to per-event timestamp medians.',
    },
    caseSensitivity:
      'source values are matched CASE-SENSITIVELY by the engine — write them exactly as listed ' +
      '(lowercase with underscores). Event types match case-insensitively.',
    operators: [
      'equals', 'not_equals', 'contains', 'not_contains', 'regex', 'not_regex',
      'gt', 'lt', 'gte', 'lte', 'exists', 'not_exists', 'count_gte', 'count_per_group_gte', 'in', 'not_in',
    ],
    operatorNotes:
      'String comparisons are case-insensitive; regex runs case-insensitive with a 1s timeout ' +
      '(.NET regex syntax); in/not_in take a comma-separated value list. An operator the engine ' +
      'does not know silently evaluates to FALSE in production — validate_rule catches this.',
    required:
      'required=true conditions are the firing core: ALL of them must match or the rule produces ' +
      'nothing. required=false conditions are optional reinforcers whose evidence feeds ' +
      'confidenceFactors. A rule where NO condition matched never fires, even if all conditions ' +
      'are optional.',
    preconditions:
      'Optional gate BEFORE conditions: source must be "event_data"; all preconditions are ' +
      'AND-combined and a failing one silently skips the rule (no finding, no UI card). Use for ' +
      'applicability ("skip on virtual machines", "only when marker event absent via not_exists").',
    suppressByEvent:
      'On a condition, suppressByEvent {eventType, joinField} drops matches that a resolving event ' +
      'shares the joinField value with — e.g. app_install_failed suppressed by a later ' +
      'app_install_completed of the same appId.',
    confidence: {
      model:
        'If all required conditions match (and at least one condition matched), score = ' +
        'baseConfidence + sum of weights of matched confidenceFactors, capped at 100. The rule ' +
        'fires only if score >= confidenceThreshold.',
      factorConditions:
        'A factor\'s condition must be EXACTLY one of these shapes (anything else silently never ' +
        'matches): "exists" (the condition signal named by factor.signal matched), "count >= N" ' +
        '(factor.signal is an EVENT TYPE here — counts events of that type, case-sensitive), ' +
        '"phase_duration > N" (seconds, requires a matched phase_duration condition).',
      bestPractices:
        'baseConfidence 40-60 for single-signal rules, 50-70 for correlations; set ' +
        'confidenceThreshold at or slightly below baseConfidence so the rule can fire without ' +
        'factors; weight optional reinforcers 10-20 each.',
    },
    interpolation:
      'explanation and remediation text may contain {{token}} placeholders, resolved from the ' +
      'matched evidence at display time: first a matched condition whose dataField equals the ' +
      'token, then the auto-captured fields appId / appName / errorPatternId / errorCode / ' +
      'exitCode / status from the matched events, then a condition SIGNAL name. Unresolved tokens ' +
      'stay visible as {{token}} so typos are noticeable. test_analyze_rule returns the ' +
      'interpolated preview for a real session.',
    markSessionAsFailed:
      'markSessionAsFailedDefault=true escalates a firing rule to a terminal Failed session ' +
      'status (KO criterion). Use sparingly — only for findings that genuinely mean the ' +
      'enrollment failed.',
    evaluateOn:
      'WHEN the rule is evaluated. Absent = ["enrollment_end"] — the terminal-only default. ' +
      'Interim triggers evaluate the rule BEFORE the session is terminal: "whiteglove_sealed" ' +
      '(first genuine WhiteGlove seal, technician still at the bench) and "on_event:<eventType>" ' +
      '(an ingest batch contained that event type; pick a rare, problem-indicating type — ' +
      'high-frequency telemetry types are HARD-BLOCKED: the backend rejects them on save and the ' +
      'runtime ignores them, see guardrails blockedInterimTriggerEventTypes). Interim semantics: ' +
      'markSessionAsFailed is SUPPRESSED, no stats ' +
      'are recorded, findings notify once per (session, rule) and render as "preliminary" until ' +
      'the enrollment-end pass finalizes or resolves them. CRITICAL: interim-enabled rules need ' +
      'MONOTONIC conditions — a not_exists precondition on enrollment_complete / enrollment_failed ' +
      '/ session_timeout passes trivially mid-run, so gate on repetition (count factors + a ' +
      'threshold above baseConfidence) instead. validate_rule lints all of this.',
    template: {
      ruleId: 'ANALYZE-CUSTOM-101',
      title: 'Repeated install failures of the same app',
      description: 'Detects an app failing to install 3+ times in one enrollment.',
      severity: 'high',
      category: 'apps',
      version: '1.0.0',
      author: 'Contoso IT',
      enabled: true,
      trigger: 'single',
      baseConfidence: 55,
      conditions: [
        {
          signal: 'app_retry_storm',
          source: 'event_count',
          eventType: 'app_install_failed',
          dataField: 'appId',
          operator: 'count_per_group_gte',
          value: '3',
          required: true,
        },
      ],
      confidenceFactors: [
        { signal: 'enrollment_failed', condition: 'count >= 1', weight: 15 },
      ],
      confidenceThreshold: 50,
      explanation: 'App {{appName}} failed to install repeatedly (last error {{errorCode}}).',
      remediation: [
        { title: 'Check the app package', steps: ['Review the install command line', 'Test the package on a reference device'] },
      ],
      tags: ['apps', 'retry'],
    },
  },

  enums: {
    gatherCategories: ['network', 'identity', 'apps', 'device', 'esp', 'enrollment'],
    analyzeCategories: ['network', 'identity', 'enrollment', 'apps', 'esp', 'device', 'security'],
    analyzeSeverities: ['info', 'warning', 'high', 'critical'],
    gatherOutputSeverities: ['Info', 'Warning', 'Error', 'Critical'],
    gatherCollectorTypes: ['registry', 'eventlog', 'wmi', 'file', 'json', 'xml', 'command_allowlisted', 'logparser'],
    gatherTriggers: ['startup', 'phase_change', 'phase_exit', 'interval', 'on_event'],
    analyzeConditionSources: ['event_type', 'event_data', 'event_data_array', 'phase_duration', 'event_count', 'app_install_duration', 'event_correlation', 'clock_skew'],
    analyzeEvaluateOnTriggers: ['enrollment_end', 'whiteglove_sealed', 'on_event:<eventType>'],
  },

  commonPitfalls: [
    'Referencing an event type that never occurs in real sessions — check get_resource(name="event_types") and use test_analyze_rule\'s per-condition matchingEventCount to verify.',
    'confidenceThreshold higher than baseConfidence plus all factor weights — the rule can mathematically never fire.',
    'Unanchored allow-list regex with not_regex — anchors (^, \\b) are mandatory or impostor values pass.',
    'A confidenceFactor condition like "count > 3" or "Count >= 3" — only the exact shapes "exists", "count >= N", "phase_duration > N" evaluate.',
    'Gather targets outside the guardrail allowlists — the agent blocks them at run time; validate_rule checks this up front.',
    'Testing a logparser regex in a JS/PHP/Python tester and assuming it behaves the same on the device — the agent uses the .NET engine and matches case-SENSITIVELY. Use test_log_pattern, which runs the exact agent semantics.',
    'Running a plain-text log through the default cmtrace format — every line fails CMTrace parsing and nothing ever matches. Set parameters.format="text" (test_log_pattern reports this explicitly).',
    'Duplicate condition signals — evidence is keyed by signal, the second overwrites the first.',
    'Expecting gather events in sessions that ran BEFORE the gather rule was deployed — dry-run against a recent session.',
    'Adding an interim evaluateOn trigger to a rule whose only suppression is a not_exists precondition on enrollment_complete — mid-run that gate passes trivially and the rule fires on healthy in-flight sessions.',
  ],
} as const;
