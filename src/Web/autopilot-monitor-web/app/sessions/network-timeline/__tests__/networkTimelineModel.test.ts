import { describe, expect, it } from 'vitest';
import {
  buildNetworkModel,
  detectHotspot,
  NetSegment,
} from '../networkTimelineModel';
import type { EnrollmentEvent, Session } from '@/types';

const T0 = Date.parse('2026-08-21T10:00:00.000Z');
const MIN = 60_000;
const iso = (offsetMs: number) => new Date(T0 + offsetMs).toISOString();

function ev(sequence: number, eventType: string, offsetMs: number, data?: Record<string, unknown>): EnrollmentEvent {
  return {
    eventId: `e${sequence}`,
    sessionId: 's',
    timestamp: iso(offsetMs),
    eventType,
    severity: 'Info',
    source: 'test',
    phase: -1,
    message: '',
    sequence,
    data,
  } as EnrollmentEvent;
}

const session = {
  sessionId: 's',
  tenantId: 't',
  startedAt: iso(0),
  enrollmentType: 'v1',
} as unknown as Session;

const ethernetInfo = (seq: number, offsetMs: number) =>
  ev(seq, 'network_interface_info', offsetMs, {
    connectionType: 'Ethernet',
    linkSpeedMbps: 1000,
    gateways: '10.0.0.1',
  });

const wifiInfo = (seq: number, offsetMs: number) =>
  ev(seq, 'network_interface_info', offsetMs, {
    connectionType: 'WiFi',
    linkSpeedMbps: 866,
    gateways: '10.0.0.1',
  });

describe('clock-era correction', () => {
  it('rebases the axis after a live clock step and adds a marker', () => {
    // Clock stepped +1h at real minute 5 — later raw timestamps are 1h ahead.
    const events = [
      ethernetInfo(1, 0),
      ev(2, 'log_entry', 5 * MIN),
      ev(3, 'system_clock_changed', 65 * MIN, {
        timeDeltaMs: 3_600_000,
        oldTime: iso(5 * MIN),
        newTime: iso(65 * MIN),
        backfilled: false,
      }),
      ev(4, 'log_entry', 70 * MIN),
    ];
    const model = buildNetworkModel(session, events)!;
    expect(model).not.toBeNull();
    // True elapsed time is 10 minutes, not 70.
    expect(model.t1 - model.t0).toBe(10 * MIN);
    const clockMarker = model.lifeMarkers.find((m) => m.label.startsWith('Clock +'));
    expect(clockMarker).toBeDefined();
    expect(clockMarker!.label).toBe('Clock +1h 0m');
    expect(clockMarker!.detail).toContain('axis rebased');
    expect(clockMarker!.t).toBe(T0 + 5 * MIN);
    expect(model.clockChangeCount).toBe(1);
  });

  it('does not rebase for a backfilled step but still shows a marker', () => {
    const events = [
      ethernetInfo(1, 0),
      ev(2, 'system_clock_changed', 5 * MIN, {
        timeDeltaMs: 3_600_000,
        oldTime: iso(-60 * MIN),
        newTime: iso(0),
        backfilled: true,
        timeCreated: iso(2 * MIN),
      }),
      ev(3, 'log_entry', 10 * MIN),
    ];
    const model = buildNetworkModel(session, events)!;
    expect(model.t1 - model.t0).toBe(10 * MIN);
    const clockMarker = model.lifeMarkers.find((m) => m.label.startsWith('Clock +'));
    expect(clockMarker).toBeDefined();
    expect(clockMarker!.detail).toContain('before agent start');
  });

  it('ignores sub-30s steps (NTP nudges)', () => {
    const events = [
      ethernetInfo(1, 0),
      ev(2, 'system_clock_changed', 5 * MIN, { timeDeltaMs: 10_000, backfilled: false }),
      ev(3, 'log_entry', 10 * MIN),
    ];
    const model = buildNetworkModel(session, events)!;
    expect(model.clockChangeCount).toBe(0);
    expect(model.lifeMarkers.some((m) => m.label.startsWith('Clock'))).toBe(false);
    expect(model.t1 - model.t0).toBe(10 * MIN);
  });
});

describe('sleep episodes', () => {
  it('carves an asleep segment out of the active network', () => {
    const events = [
      wifiInfo(1, 0),
      ev(2, 'wifi_signal_info', 1 * MIN, { wifiSsid: 'CorpNet', wifiSignalPercent: 80 }),
      ev(3, 'system_sleep_episode', 20 * MIN, {
        kind: 'modern_standby',
        enteredAt: iso(10 * MIN),
        exitedAt: iso(20 * MIN),
        durationSeconds: 600,
        onAcPower: false,
        backfilled: false,
      }),
      ev(4, 'log_entry', 30 * MIN),
    ];
    const model = buildNetworkModel(session, events)!;
    const kinds = model.segments.map((s) => s.kind);
    expect(kinds).toEqual(['wifi', 'asleep', 'wifi']);
    expect(model.segments[1].start).toBe(T0 + 10 * MIN);
    expect(model.segments[1].end).toBe(T0 + 20 * MIN);
    expect(model.segments[1].sleepKind).toBe('modern_standby');
    expect(model.segments[1].onAcPower).toBe(false);
    expect(model.asleepMs).toBe(10 * MIN);
    // Both wifi pieces keep the same identity (same network, one legend entry)
    expect(model.segments[0].identity).toBe(model.segments[2].identity);
    expect(model.distinctNetworks.size).toBe(1);
  });

  it('applies era correction to episode boundaries carried by a post-step event', () => {
    // Step +1h at real minute 5; the sleep episode event (emitted at wake,
    // raw minute 80 = real 20) carries raw enteredAt/exitedAt of 70/80.
    const events = [
      wifiInfo(1, 0),
      ev(2, 'system_clock_changed', 65 * MIN, {
        timeDeltaMs: 3_600_000,
        oldTime: iso(5 * MIN),
        newTime: iso(65 * MIN),
        backfilled: false,
      }),
      ev(3, 'system_sleep_episode', 80 * MIN, {
        kind: 'sleep',
        enteredAt: iso(70 * MIN),
        exitedAt: iso(80 * MIN),
        backfilled: false,
      }),
      ev(4, 'log_entry', 85 * MIN),
    ];
    const model = buildNetworkModel(session, events)!;
    const asleep = model.segments.find((s) => s.kind === 'asleep')!;
    expect(asleep.start).toBe(T0 + 10 * MIN);
    expect(asleep.end).toBe(T0 + 20 * MIN);
    expect(model.t1 - model.t0).toBe(25 * MIN);
  });
});

describe('hotspot confidence', () => {
  const wifiSeg = (ssid: string | undefined, gateway: string): NetSegment => ({
    start: 0,
    end: 1,
    kind: 'wifi',
    ssid,
    gateway,
    identity: 'x',
  });

  it('subnet-only evidence stays a hedged hint', () => {
    const hint = detectHotspot(wifiSeg('Daniel Langhof', '172.20.10.1'))!;
    expect(hint.vendor).toBe('Apple');
    expect(hint.confident).toBe(false);
  });

  it('an iPhone-named SSID is confident', () => {
    const hint = detectHotspot(wifiSeg("Ruben Ryhan's iPhone", '172.20.10.1'))!;
    expect(hint.confident).toBe(true);
    const ssidOnly = detectHotspot(wifiSeg('iPhone von Max', '192.168.0.1'))!;
    expect(ssidOnly.vendor).toBe('Apple');
    expect(ssidOnly.confident).toBe(true);
  });
});
