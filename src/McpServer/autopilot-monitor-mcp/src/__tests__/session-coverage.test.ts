/**
 * Observation-coverage block of get_session_summary: a pure fold over the health/lifecycle
 * events the agent already emits. Every branch here mirrors one agent emit site — the payload
 * keys are the ones the agent writes (see session-coverage.ts), typed loosely because half of
 * them arrive as strings (Dictionary<string,string> posts) and half as numbers.
 */
import { describe, it, expect } from 'vitest';
import { buildSessionCoverage, COVERAGE_EVENT_FIELDS, COVERAGE_EVENT_TYPES } from '../session-coverage.js';
import { SUMMARY_EVENT_FIELDS } from '../tools/shared.js';

const T0 = Date.UTC(2026, 8, 3, 15, 0, 0);
const at = (offsetSec: number): string => new Date(T0 + offsetSec * 1000).toISOString();

const ev = (eventType: string, offsetSec: number, data: Record<string, unknown> = {}, extra: Record<string, unknown> = {}) => ({
  eventType,
  timestamp: at(offsetSec),
  severity: 'Info',
  data,
  ...extra,
});

const SESSION = { startedAt: at(0) };

describe('buildSessionCoverage — clean session', () => {
  it('reports full coverage with no gaps', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 5, { previousExitType: '' }),
      ev('esp_phase_changed', 60),
      ev('diagnostics_uploaded', 900, { blobName: 'x.zip', destination: 'Hosted' }),
    ]);
    expect(c.gaps).toEqual([]);
    expect(c.window.observedFrom).toBe(at(5));
    expect(c.window.startedAtBackdatedSeconds).toBe(5);
    expect(c.window.agentStarts).toBe(1);
    expect(c.window.previousExitTypes).toEqual([]);
    expect(c.window.lateStart).toBeNull();
    expect(c.window.historicReplaySuppressed).toBeNull();
    expect(c.imeTracker).toEqual({ degraded: false });
    expect(c.collectors).toEqual([]);
    expect(c.upload).toEqual({ spoolPressure: false, poisonedItems: 0, blocked: false, backpressureEpisodes: 0, diskSpaceLow: false });
    // Older agents report no packaging counters — status only, no truncated flag.
    expect(c.diagnostics).toEqual({ status: 'uploaded' });
  });

  it('ignores events outside the coverage set and tolerates missing data', () => {
    const c = buildSessionCoverage({ startedAt: null }, [
      { eventType: 'app_install_failed', timestamp: at(1) },
      { eventType: 'agent_started', timestamp: at(2) },
    ]);
    expect(c.window.sessionStartedAt).toBeNull();
    expect(c.window.startedAtBackdatedSeconds).toBeNull();
    expect(c.gaps).toEqual([]);
  });
});

describe('buildSessionCoverage — observation window', () => {
  it('flags a missing agent_started', () => {
    const c = buildSessionCoverage(SESSION, [ev('esp_phase_changed', 10)]);
    expect(c.window.observedFrom).toBeNull();
    expect(c.diagnostics.status).toBe('not_collected');
    expect(c.gaps).toHaveLength(1);
    expect(c.gaps[0]).toContain('No agent_started');
  });

  it('flags a back-dated session start (IME replay reconstruction) beyond a minute', () => {
    const c = buildSessionCoverage({ startedAt: at(-3600) }, [ev('agent_started', 0)]);
    expect(c.window.startedAtBackdatedSeconds).toBe(3600);
    expect(c.gaps.some((g) => g.includes('3600 s before the first agent_started'))).toBe(true);
  });

  it('never reports a negative back-date when startedAt is after the first start', () => {
    const c = buildSessionCoverage({ startedAt: at(100) }, [ev('agent_started', 0)]);
    expect(c.window.startedAtBackdatedSeconds).toBe(0);
    expect(c.gaps).toEqual([]);
  });

  it('counts restarts and their previous exit types, earliest start first regardless of input order', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 700, { previousExitType: 'Crash' }),
      ev('agent_started', 0),
      ev('agent_started', 300, { previousExitType: 'Reboot' }),
      ev('agent_started', 900, { previousExitType: 'Crash' }),
    ]);
    expect(c.window.observedFrom).toBe(at(0));
    expect(c.window.agentStarts).toBe(4);
    expect(c.window.previousExitTypes).toEqual(['Reboot', 'Crash']);
    expect(c.gaps).toEqual([expect.stringContaining('started 4 times (previous exit: Reboot, Crash)')]);
  });

  it('surfaces agent_late_start even at Info severity (successful session) with the agent note', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('agent_late_start', 200, { bootToAgentStartSeconds: '1800', agentUptimeSeconds: 120, outcome: 'Succeeded', note: 'Agent arrived after the work was done.' }),
    ]);
    expect(c.window.lateStart).toEqual({ bootToAgentStartSeconds: 1800, agentUptimeSeconds: 120, outcome: 'Succeeded', note: 'Agent arrived after the work was done.' });
    expect(c.gaps).toEqual([expect.stringMatching(/^Low observation coverage \(agent started 1800 s after boot, ran 120 s\): Agent arrived after the work was done\.$/)]);
  });

  it('surfaces suppressed historic IME replay with the earliest rejected timestamp', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('historic_ime_replay_detected', 1, { decision: 'historic_ime_replay_suppressed', earliestRejectedSourceTimestamp: '2026-08-01T00:00:00.0000000Z' }),
    ]);
    expect(c.window.historicReplaySuppressed).toEqual({ earliestRejectedSourceTimestamp: '2026-08-01T00:00:00.0000000Z' });
    expect(c.gaps).toEqual([expect.stringContaining('earliest 2026-08-01T00:00:00.0000000Z')]);
  });
});

describe('buildSessionCoverage — IME log tracker', () => {
  it('projects ime_tracker_degraded with a calibrated budget-break line', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('ime_tracker_degraded', 50, { file: 'AgentExecutor.log', oversizedLines: 0, regexTimeouts: 0, lineBudgetBreaks: 1, unanchoredPatterns: 0, firstSkippedPattern: 'PS-SCRIPT-GENERATED' }, { severity: 'Warning' }),
    ]);
    expect(c.imeTracker).toEqual({
      degraded: true,
      file: 'AgentExecutor.log',
      firstSkippedPattern: 'PS-SCRIPT-GENERATED',
      lineBudgetBreaks: 1,
      regexTimeouts: 0,
      oversizedLines: 0,
      unanchoredPatterns: 0,
    });
    expect(c.gaps).toEqual([
      'IME log tracker skipped the remaining patterns on 1 line(s) (first skipped: PS-SCRIPT-GENERATED) in AgentExecutor.log: the absence of an IME-derived event is not fully provable for this session.',
    ]);
  });

  it('prefers whole-session totals from ime_pattern_hits over the first-pass counters', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('ime_tracker_degraded', 50, { file: 'AppWorkload.log', lineBudgetBreaks: 1, regexTimeouts: 0, oversizedLines: 0 }),
      ev('ime_pattern_hits', 900, { linesRead: 12000, lineBudgetBreaks: 7, regexTimeouts: 2, oversizedLines: 1 }, { severity: 'Debug' }),
    ]);
    expect(c.imeTracker.sessionTotals).toEqual({ linesRead: 12000, lineBudgetBreaks: 7, regexTimeouts: 2, oversizedLines: 1 });
    expect(c.gaps[0]).toContain('skipped the remaining patterns on 7 line(s)');
    expect(c.gaps[0]).toContain('gave up on 2 line(s) after a regex timeout');
    expect(c.gaps[0]).toContain('dropped 1 oversized line(s)');
  });

  it('keeps session totals on a healthy tracker without declaring a gap', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('ime_pattern_hits', 900, { linesRead: 800, lineBudgetBreaks: 0, regexTimeouts: 0, oversizedLines: 0 }),
    ]);
    expect(c.imeTracker).toEqual({ degraded: false, sessionTotals: { linesRead: 800, lineBudgetBreaks: 0, regexTimeouts: 0, oversizedLines: 0 } });
    expect(c.gaps).toEqual([]);
  });
});

describe('buildSessionCoverage — collectors, upload, disk', () => {
  it('deduplicates collector_degraded by (collector, reason) and keeps the first occurrence', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('collector_degraded', 20, { collector: 'HelloTracker', reason: 'watcher_arm_failed', errorType: 'UnauthorizedAccessException' }, { source: 'HelloTracker' }),
      ev('collector_degraded', 25, { collector: 'HelloTracker', reason: 'watcher_arm_failed' }),
      ev('collector_degraded', 30, { collector: 'WindowsUpdateTracker', reason: 'subscription_failed' }),
    ]);
    expect(c.collectors).toEqual([
      { collector: 'HelloTracker', reason: 'watcher_arm_failed', errorType: 'UnauthorizedAccessException', at: at(20) },
      { collector: 'WindowsUpdateTracker', reason: 'subscription_failed', errorType: null, at: at(30) },
    ]);
    expect(c.gaps).toEqual([
      expect.stringContaining('Collector HelloTracker degraded (watcher_arm_failed, UnauthorizedAccessException)'),
      expect.stringContaining('Collector WindowsUpdateTracker degraded (subscription_failed)'),
    ]);
  });

  it('folds spool pressure, poison (summed itemCount), block, backpressure and low disk', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('spool_pressure_detected', 100, { pendingItemCount: 2400, pendingBytes: 6000000 }),
      ev('telemetry_upload_poisoned', 110, { kind: 'Oversize', itemCount: 1 }),
      ev('telemetry_upload_poisoned', 120, { kind: 'BackendDeclared', itemCount: '3' }),
      ev('telemetry_upload_blocked', 130, { reason: 'permanent 4xx' }),
      ev('ingress_backpressure', 140, { origin: 'ime' }),
      ev('ingress_backpressure', 141, { origin: 'ime' }),
      ev('disk_space_low', 150, { disk_free_gb: 1.4, disk_total_gb: 256, threshold_gb: 2 }),
    ]);
    expect(c.upload).toEqual({ spoolPressure: true, poisonedItems: 4, blocked: true, backpressureEpisodes: 2, diskSpaceLow: true });
    expect(c.gaps).toEqual([
      expect.stringContaining('Telemetry spool pressure (2400 items pending)'),
      expect.stringContaining('4 telemetry item(s) were quarantined as poison and never uploaded (Oversize, BackendDeclared)'),
      expect.stringContaining('Telemetry upload was blocked'),
      expect.stringContaining('System drive ran low on space (1.4 GB free)'),
    ]);
  });

  it('counts a poison event without itemCount as one item', () => {
    const c = buildSessionCoverage(SESSION, [ev('agent_started', 0), ev('telemetry_upload_poisoned', 1, {})]);
    expect(c.upload.poisonedItems).toBe(1);
  });
});

describe('buildSessionCoverage — diagnostics package', () => {
  it('reports packaging counters and a truncation gap (no paths)', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('diagnostics_uploaded', 900, {
        blobName: 'x.zip',
        truncated: 'true',
        includedFiles: 412,
        includedBytes: 123456789,
        skippedFiles: 3,
        skippedByReason: { size: 2, total: 1, reparse: 0 },
        problemsByKind: { copy: 1 },
      }),
    ]);
    expect(c.diagnostics).toEqual({
      status: 'uploaded',
      truncated: true,
      includedFiles: 412,
      skippedFiles: 3,
      skippedByReason: { size: 2, total: 1 },
      problemsByKind: { copy: 1 },
    });
    expect(c.gaps).toEqual([
      'Diagnostics ZIP is truncated: 3 file(s) skipped by the packaging caps (size: 2, total: 1); the full list is in _TRUNCATED.txt inside the ZIP.',
      'Diagnostics packaging hit problems (copy: 1): those files are missing from the ZIP; package-manifest.txt names them.',
    ]);
  });

  it('reports a complete package with counters as no gap', () => {
    const c = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('diagnostics_uploaded', 900, { truncated: false, includedFiles: 40, includedBytes: 1000, skippedFiles: 0, skippedByReason: {}, problemsByKind: {} }),
    ]);
    expect(c.diagnostics).toEqual({ status: 'uploaded', truncated: false, includedFiles: 40, skippedFiles: 0 });
    expect(c.gaps).toEqual([]);
  });

  it('distinguishes a configuration skip from an upload failure', () => {
    const skipped = buildSessionCoverage(SESSION, [ev('agent_started', 0), ev('diagnostics_upload_failed', 900, { errorCode: 'diagnostics_skipped_mode_off' })]);
    expect(skipped.diagnostics).toEqual({ status: 'skipped', errorCode: 'diagnostics_skipped_mode_off' });
    expect(skipped.gaps).toEqual([expect.stringContaining('skipped by configuration (diagnostics_skipped_mode_off)')]);

    const failed = buildSessionCoverage(SESSION, [ev('agent_started', 0), ev('diagnostics_upload_failed', 900, { errorCode: 'upload_5xx' })]);
    expect(failed.diagnostics).toEqual({ status: 'failed', errorCode: 'upload_5xx' });
    expect(failed.gaps).toEqual([expect.stringContaining('Diagnostics upload failed (upload_5xx)')]);
  });

  it('lets a later upload win over an earlier failure (retry) and flags a collection that never finished', () => {
    const retried = buildSessionCoverage(SESSION, [
      ev('agent_started', 0),
      ev('diagnostics_upload_failed', 800, { errorCode: 'upload_5xx' }),
      ev('diagnostics_uploaded', 900, { blobName: 'x.zip' }),
    ]);
    expect(retried.diagnostics.status).toBe('uploaded');
    expect(retried.gaps).toEqual([]);

    const died = buildSessionCoverage(SESSION, [ev('agent_started', 0), ev('diagnostics_collecting', 800, { mode: 'Always' })]);
    expect(died.diagnostics.status).toBe('not_collected');
    expect(died.gaps).toEqual([expect.stringContaining('neither an upload nor a failure was reported')]);
  });
});

describe('buildSessionCoverage — apps without a terminal state', () => {
  // The shutdown emit of the termination handler: what was still in flight when the agent left.
  const shutdownSummary = ev('app_tracking_summary', 900, { installingNames: ['Suite'], downloadingNames: [], pendingNames: ['Other'] });

  it("names the apps still in flight in the agent's last summary on a terminal session", () => {
    const c = buildSessionCoverage({ startedAt: at(0), status: 'Succeeded' }, [
      ev('agent_started', 5),
      ev('app_tracking_summary', 300, { installingNames: ['Early'], downloadingNames: ['Later'] }),
      shutdownSummary,
    ]);
    expect(c.apps).toEqual({ stillInstalling: ['Suite'], stillDownloading: [] });
    expect(c.gaps).toEqual([expect.stringContaining('1 app(s) still installing when the agent stopped observing (Suite)')]);
  });

  it('reports nothing while the session is still live, even with apps in flight', () => {
    const c = buildSessionCoverage({ startedAt: at(0), status: 'InProgress' }, [ev('agent_started', 5), shutdownSummary]);
    expect(c.apps).toEqual({ stillInstalling: [], stillDownloading: [] });
    expect(c.gaps).toEqual([]);
  });

  it("stays silent when the last summary has nothing in flight (apps that never started are the starved rule's finding)", () => {
    const c = buildSessionCoverage({ startedAt: at(0), status: 'Failed' }, [
      ev('agent_started', 5),
      ev('app_tracking_summary', 900, { installingNames: [], pendingNames: ['Other'] }),
    ]);
    expect(c.apps).toEqual({ stillInstalling: [], stillDownloading: [] });
    expect(c.gaps).toEqual([]);
  });
});

describe('buildSessionCoverage — scripts without a terminal state', () => {
  // Shape of session 2ebcd480: the session's last event was a platform script's start line.
  // There is no shutdown summary for scripts — the stream itself is the evidence.
  const started = (policyId: string, at: number, scriptType = 'platform') => ev('script_started', at, { policyId, scriptType });
  const finished = (policyId: string, at: number, scriptType = 'platform') => ev('script_completed', at, { policyId, scriptType, exitCode: '0' });

  it('names the scripts whose start line never got a result on a terminal session', () => {
    const c = buildSessionCoverage({ startedAt: at(0), status: 'Succeeded' }, [
      ev('agent_started', 5),
      started('p-a', 900), finished('p-a', 903),
      started('p-b', 904), ev('script_failed', 907, { policyId: 'p-b', scriptType: 'platform' }),
      started('p-last', 910),
    ]);
    expect(c.scripts).toEqual({ stillRunning: [{ policyId: 'p-last', scriptType: 'platform' }] });
    expect(c.gaps).toEqual([expect.stringContaining('1 script(s) still running when the agent stopped observing (platform p-last)')]);
  });

  it('reports nothing while the session is still live, even with a script in flight', () => {
    const c = buildSessionCoverage({ startedAt: at(0), status: 'InProgress' }, [ev('agent_started', 5), started('p-last', 910)]);
    expect(c.scripts).toEqual({ stillRunning: [] });
    expect(c.gaps).toEqual([]);
  });

  it('a start after its own earlier result is a second run and counts as still running; snake_case keys are read too', () => {
    const c = buildSessionCoverage({ startedAt: at(0), status: 'Failed' }, [
      ev('agent_started', 5),
      finished('r1', 100, 'remediation'),
      ev('script_started', 200, { policy_id: 'r1', script_type: 'remediation' }),
    ]);
    expect(c.scripts).toEqual({ stillRunning: [{ policyId: 'r1', scriptType: 'remediation' }] });
    expect(c.gaps).toHaveLength(1);
  });
});

describe('coverage wiring', () => {
  it('SUMMARY_EVENT_FIELDS carries every coverage slice, as data.<key> entries only', () => {
    const fields = SUMMARY_EVENT_FIELDS.split(',');
    for (const f of COVERAGE_EVENT_FIELDS.split(',')) {
      expect(f.startsWith('data.')).toBe(true);
      expect(fields).toContain(f);
    }
    // The scripts block also reads scriptType/script_type — they ride in the summary's own
    // list for the benign-detection guard, so pin them here rather than duplicating them.
    expect(fields).toContain('data.scriptType');
    expect(fields).toContain('data.script_type');
    expect(fields).not.toContain('data');
  });

  it('every coverage event type is read by the builder (no dead member)', () => {
    // A type in the set that the fold never consults would be a maintenance trap: keep the
    // set equal to what the builder actually switches on.
    const consulted = [
      'agent_started', 'agent_late_start', 'historic_ime_replay_detected', 'ime_tracker_degraded', 'ime_pattern_hits',
      'collector_degraded', 'spool_pressure_detected', 'telemetry_upload_poisoned', 'telemetry_upload_blocked',
      'ingress_backpressure', 'disk_space_low', 'diagnostics_collecting', 'diagnostics_uploaded', 'diagnostics_upload_failed',
      'app_tracking_summary',
      'script_started', 'script_completed', 'script_failed',
    ];
    expect([...COVERAGE_EVENT_TYPES].sort()).toEqual([...consulted].sort());
  });
});
