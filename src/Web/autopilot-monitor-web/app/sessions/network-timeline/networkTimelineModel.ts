// ─────────────────────────────────────────────────────────────────────────────
// Network timeline — domain model.
//
// Derives network segments (Ethernet / WiFi SSIDs / offline gaps) from the
// session's event stream, assigns fixed-order categorical colors, builds the
// piecewise time→x scale and classifies probable smartphone hotspots from
// well-known vendor subnets.
//
// Clock correction: system_clock_changed events (timeDeltaMs is authoritative)
// split the stream into clock eras; every event timestamp after a live-observed
// step is rebased by the cumulative delta, so the axis shows true elapsed time.
// Backfilled steps (replayed from the event log at agent start — they happened
// before the session's event stream) only get a marker, never a rebase.
// Sleep: system_sleep_episode events (emitted retroactively at wake) overlay
// the affected time range as their own 'asleep' segment kind instead of
// attributing the quiet period to the active network.
// ─────────────────────────────────────────────────────────────────────────────

import { V1_PHASE_NAMES, V2_PHASE_NAMES } from '@/app/sessions/utils/phaseConstants';
import type { EnrollmentEvent, Session } from '@/types';

// ── Types ────────────────────────────────────────────────────────────────────

export type SegmentKind = 'ethernet' | 'wifi' | 'offline' | 'asleep' | 'unknown';

export interface HotspotHint {
  vendor: 'Apple' | 'Windows' | 'Android';
  reason: string;
  /**
   * true when the SSID itself matches a typical phone-hotspot name
   * (e.g. "xyz's iPhone") — strong enough to drop the "?" from the badge.
   * false = subnet-only evidence, shown as "Hotspot?".
   */
  confident: boolean;
}

export interface NetSegment {
  start: number;
  end: number;
  kind: SegmentKind;
  ssid?: string;
  ssidInferred?: boolean;
  ip?: string;
  gateway?: string;
  linkSpeedMbps?: number;
  adapterDescription?: string;
  signalPercent?: number;
  radioType?: string;
  /**
   * Why signal/radio are absent, when the agent could tell — currently only
   * 'location_services_off' (Windows 11 24H2+ gates the native WLAN API behind
   * precise-location consent). Set from wifi_signal_info; never guessed.
   */
  dataLimitedReason?: string;
  hotspot?: HotspotHint;
  /** 'asleep' segments only: sleep/hibernate/modern_standby + wake details. */
  sleepKind?: string;
  wakeSourceText?: string;
  onAcPower?: boolean;
  identity: string;
}

export interface PhaseBand {
  start: number;
  end: number;
  name: string;
}

export interface CheckMarker {
  t: number;
  ok: boolean;
  reachable: number;
  total: number;
  results: { endpoint: string; reachable: boolean; latencyMs?: number; httpStatus?: number; error?: string }[];
}

export interface LifeMarker {
  t: number;
  label: string;
  /** Optional extra tooltip line (clock markers: old→new time, reason). */
  detail?: string;
}

export interface NetworkModel {
  t0: number;
  t1: number;
  segments: NetSegment[];
  colors: Map<string, string>;
  scale: { tB: number[]; xB: number[]; x: (t: number) => number };
  phases: PhaseBand[];
  checks: CheckMarker[];
  lifeMarkers: LifeMarker[];
  networkEvents: EnrollmentEvent[];
  offlineMs: number;
  asleepMs: number;
  clockChangeCount: number;
  distinctNetworks: Set<string>;
  switchCount: number;
  hotspotDetected: boolean;
}

// ── Constants ────────────────────────────────────────────────────────────────

// Fixed-order categorical palette (validated light+dark, dataviz skill):
// Ethernet always blue; each distinct WiFi network takes the next slot.
export const ETHERNET_COLOR = '#3b82f6';
export const WIFI_COLORS = ['#16a34a', '#8b5cf6', '#d97706', '#0891b2'];
export const WIFI_OVERFLOW_COLOR = '#64748b';
export const OFFLINE_COLOR = '#dc2626';
export const ASLEEP_COLOR = '#64748b';
export const UNKNOWN_COLOR = '#9ca3af';

export const NETWORK_EVENT_TYPES = new Set([
  'network_interface_info',
  'network_state_change',
  'network_connectivity_check',
  'wifi_signal_info',
  'network_adapters',
  'network_bandwidth_estimate',
  'dns_configuration',
  'proxy_configuration',
  'outbound_ip',
  'system_clock_changed',
  'system_sleep_episode',
]);

// ── Formatting helpers ───────────────────────────────────────────────────────

export function fmtTime(ms: number): string {
  return new Date(ms).toLocaleTimeString(undefined, { hour12: false });
}

export function fmtDuration(ms: number): string {
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${m % 60}m`;
  // Day scale (e.g. a CMOS clock jumping years): minutes are noise here.
  const d = Math.floor(h / 24);
  return `${d}d ${h % 24}h`;
}

export function segmentTitle(seg: NetSegment): string {
  if (seg.kind === 'offline') return 'Offline';
  if (seg.kind === 'asleep') return 'Asleep';
  if (seg.kind === 'unknown') return 'Unknown';
  if (seg.kind === 'ethernet') return 'Ethernet';
  return seg.ssid ? `WiFi "${seg.ssid}"${seg.ssidInferred ? ' *' : ''}` : 'WiFi';
}

function str(v: unknown): string | undefined {
  if (typeof v !== 'string') return undefined;
  if (v === '' || v === 'n/a' || v === 'None') return undefined;
  return v;
}

function num(v: unknown): number | undefined {
  return typeof v === 'number' && isFinite(v) && v > 0 ? v : undefined;
}

// ── Hotspot heuristic ────────────────────────────────────────────────────────
// Vendor-default hotspot subnets. Apple hard-codes 172.20.10.0/28 for the
// personal hotspot — near-fingerprint precision. Windows Mobile Hotspot
// defaults to 192.168.137.1. Older Android uses 192.168.43.1 (newer versions
// randomize, so Android detection has false NEGATIVES). Always presented as
// a heuristic ("vermutlich"), never as fact.

export function detectHotspot(seg: NetSegment): HotspotHint | undefined {
  if (seg.kind !== 'wifi') return undefined;
  const gw = seg.gateway ?? '';
  const ip = seg.ip ?? '';
  // iOS default hotspot SSID is "<name>'s iPhone" (localized variants keep
  // "iPhone"). A user COULD name any AP like this, but that is atypical —
  // treat an iPhone-named SSID as confident, not just probable.
  const iphoneSsid = /iphone/i.test(seg.ssid ?? '');

  if (gw.includes('172.20.10.1') || ip.startsWith('172.20.10.')) {
    return {
      vendor: 'Apple',
      confident: iphoneSsid,
      reason: iphoneSsid
        ? 'subnet 172.20.10.x (hard-wired by iOS) and the SSID matches the typical iPhone hotspot name'
        : 'subnet 172.20.10.x — hard-wired by iOS for the personal hotspot',
    };
  }
  if (gw.includes('192.168.137.1') || ip.startsWith('192.168.137.')) {
    return { vendor: 'Windows', confident: false, reason: 'subnet 192.168.137.x — Windows mobile hotspot default' };
  }
  if (gw.includes('192.168.43.1') || ip.startsWith('192.168.43.')) {
    return { vendor: 'Android', confident: false, reason: 'subnet 192.168.43.x — classic Android hotspot default' };
  }
  if (iphoneSsid) {
    return { vendor: 'Apple', confident: true, reason: `SSID "${seg.ssid}" matches the typical iPhone hotspot name` };
  }
  return undefined;
}

// ── Segment derivation ───────────────────────────────────────────────────────

export function buildSegments(events: EnrollmentEvent[], t0: number, t1: number): NetSegment[] {
  const segs: NetSegment[] = [];

  const netInfo = events.find((e) => e.eventType === 'network_interface_info');
  const d0 = (netInfo?.data ?? {}) as Record<string, unknown>;

  let cur: NetSegment;
  if (netInfo && d0.status !== 'no_active_interface') {
    const kind: SegmentKind = d0.connectionType === 'WiFi' ? 'wifi' : 'ethernet';
    cur = {
      start: t0,
      end: t1,
      kind,
      gateway: str(d0.gateways),
      linkSpeedMbps: num(d0.linkSpeedMbps),
      adapterDescription: str(d0.adapterDescription),
      identity: '',
    };
  } else {
    cur = { start: t0, end: t1, kind: netInfo ? 'offline' : 'unknown', identity: '' };
  }

  const changes = events
    .filter((e) => e.eventType === 'network_state_change')
    .map((e) => ({ t: Date.parse(e.timestamp), data: (e.data ?? {}) as Record<string, unknown> }))
    .filter((c) => !isNaN(c.t))
    .sort((a, b) => a.t - b.t);

  for (const { t, data } of changes) {
    const tc = Math.min(Math.max(t, cur.start), t1);
    cur.end = tc;
    segs.push(cur);
    if (data.hasNetwork === false) {
      cur = { start: tc, end: t1, kind: 'offline', identity: '' };
    } else {
      cur = {
        start: tc,
        end: t1,
        kind: data.after_connectionType === 'WiFi' ? 'wifi' : 'ethernet',
        ssid: str(data.after_wifiSsid),
        ip: str(data.after_ipAddress),
        gateway: str(data.after_gateway),
        linkSpeedMbps: num(data.after_linkSpeedMbps),
        identity: '',
      };
    }
  }
  segs.push(cur);

  // Enrich WiFi segments with signal samples (SSID often only known from
  // wifi_signal_info — the NIC info event has no SSID).
  for (const e of events.filter((x) => x.eventType === 'wifi_signal_info')) {
    const t = Date.parse(e.timestamp);
    const data = (e.data ?? {}) as Record<string, unknown>;
    const seg = segs.find((s) => s.kind === 'wifi' && t >= s.start && t < s.end);
    if (!seg) continue;
    if (!seg.ssid && str(data.wifiSsid)) seg.ssid = str(data.wifiSsid);
    if (typeof data.wifiSignalPercent === 'number') seg.signalPercent = data.wifiSignalPercent;
    if (str(data.wifiRadioType)) seg.radioType = str(data.wifiRadioType);
    if (str(data.wifiDataLimitedReason)) seg.dataLimitedReason = str(data.wifiDataLimitedReason);
  }

  // The location gate is a device-level setting, not a per-connection one: it cannot flip
  // during an enrollment. So once any sample reports it, WiFi segments that never got a
  // sample of their own (a mid-session reconnect emits no wifi_signal_info) carry the same
  // explanation instead of showing an unexplained blank where the signal belongs.
  const observedLimit = segs.find((s) => s.dataLimitedReason)?.dataLimitedReason;
  if (observedLimit) {
    for (const seg of segs) {
      if (seg.kind === 'wifi' && seg.signalPercent == null) seg.dataLimitedReason = observedLimit;
    }
  }

  // Infer missing SSIDs from a sibling WiFi segment on the same gateway
  // (right after reconnect the WLAN API frequently has no SSID yet).
  for (const seg of segs) {
    if (seg.kind === 'wifi' && !seg.ssid && seg.gateway) {
      const twin = segs.find((s) => s !== seg && s.kind === 'wifi' && s.ssid && s.gateway === seg.gateway);
      if (twin) {
        seg.ssid = twin.ssid;
        seg.ssidInferred = true;
      }
    }
  }

  // Identity = color key, assigned in first-seen order (fixed, never cycled).
  for (const seg of segs) {
    seg.hotspot = detectHotspot(seg);
    if (seg.kind === 'ethernet') seg.identity = 'ethernet';
    else if (seg.kind === 'wifi') seg.identity = seg.ssid ? `wifi:${seg.ssid}` : `wifi:@${seg.gateway ?? seg.ip ?? '?'}`;
    else seg.identity = seg.kind;
  }

  return segs.filter((s) => s.end > s.start);
}

export function buildColorMap(segs: NetSegment[]): Map<string, string> {
  const map = new Map<string, string>();
  let wifiIdx = 0;
  for (const seg of segs) {
    if (map.has(seg.identity)) continue;
    if (seg.kind === 'ethernet') map.set(seg.identity, ETHERNET_COLOR);
    else if (seg.kind === 'wifi') {
      map.set(seg.identity, wifiIdx < WIFI_COLORS.length ? WIFI_COLORS[wifiIdx] : WIFI_OVERFLOW_COLOR);
      wifiIdx++;
    } else if (seg.kind === 'offline') map.set(seg.identity, OFFLINE_COLOR);
    else if (seg.kind === 'asleep') map.set(seg.identity, ASLEEP_COLOR);
    else map.set(seg.identity, UNKNOWN_COLOR);
  }
  return map;
}

// ── Piecewise time→x mapping ─────────────────────────────────────────────────
// Linear between segment boundaries; tiny segments get a minimum width so
// short offline gaps stay visible. Boundary ticks always show REAL times, so
// the axis stays honest despite the local stretch.

export const W = 1200;
// Drawing content is inset 64 units on BOTH sides: the right inset keeps the
// slanted lifecycle-marker labels inside the SVG, the left inset mirrors it
// for symmetry (and gives a left-edge marker label the same room).
export const CONTENT_LEFT = 64;
export const CONTENT_W = W - 64;
const CONTENT_SPAN = CONTENT_W - CONTENT_LEFT;
const MIN_SEG_W = 28;

export function buildScale(segs: NetSegment[]): NetworkModel['scale'] {
  const widths = segs.map((s) => s.end - s.start);
  const total = widths.reduce((a, b) => a + b, 0) || 1;
  let px = widths.map((w) => Math.max((w / total) * CONTENT_SPAN, MIN_SEG_W));
  const sum = px.reduce((a, b) => a + b, 0);
  px = px.map((w) => (w / sum) * CONTENT_SPAN);

  const tB: number[] = [segs[0]?.start ?? 0];
  const xB: number[] = [CONTENT_LEFT];
  segs.forEach((s, i) => {
    tB.push(s.end);
    xB.push(xB[i] + px[i]);
  });

  const x = (t: number): number => {
    if (t <= tB[0]) return CONTENT_LEFT;
    for (let i = 1; i < tB.length; i++) {
      if (t <= tB[i]) {
        const f = (t - tB[i - 1]) / Math.max(tB[i] - tB[i - 1], 1);
        return xB[i - 1] + f * (xB[i] - xB[i - 1]);
      }
    }
    return CONTENT_W;
  };
  return { tB, xB, x };
}

// ── Clock-era correction ─────────────────────────────────────────────────────
// system_clock_changed: data.timeDeltaMs is authoritative (the event's own
// Timestamp may be clamped server-side). Steps below 30s (NTP nudges) are
// ignored. Only live-observed steps rebase the axis — backfilled ones happened
// before the agent's event stream and would shift events that never saw the
// old clock; they get a marker only.

const MIN_CLOCK_STEP_MS = 30_000;

interface ClockChange {
  sequence: number;
  deltaMs: number;
  oldTime?: string;
  newTime?: string;
  reason?: string;
  backfilled: boolean;
  rawT: number;
  timeCreated?: string;
}

function extractClockChanges(events: EnrollmentEvent[]): ClockChange[] {
  return events
    .filter((e) => e.eventType === 'system_clock_changed')
    .map((e) => {
      const d = (e.data ?? {}) as Record<string, unknown>;
      return {
        sequence: e.sequence,
        deltaMs: typeof d.timeDeltaMs === 'number' ? d.timeDeltaMs : NaN,
        oldTime: typeof d.oldTime === 'string' ? d.oldTime : undefined,
        newTime: typeof d.newTime === 'string' ? d.newTime : undefined,
        reason: typeof d.reasonText === 'string' && d.reasonText ? d.reasonText : undefined,
        backfilled: d.backfilled === true || d.backfilled === 'true' || d.backfilled === 'True',
        rawT: Date.parse(e.timestamp),
        timeCreated: typeof d.timeCreated === 'string' ? d.timeCreated : undefined,
      };
    })
    .filter((c) => isFinite(c.deltaMs) && Math.abs(c.deltaMs) >= MIN_CLOCK_STEP_MS && !isNaN(c.rawT))
    .sort((a, b) => a.sequence - b.sequence);
}

/** Cumulative live-step delta that applies to an event at this sequence. */
function cumDeltaAt(sequence: number, liveChanges: ClockChange[]): number {
  let cum = 0;
  for (const c of liveChanges) if (c.sequence <= sequence) cum += c.deltaMs;
  return cum;
}

function correctTimestamps(events: EnrollmentEvent[], liveChanges: ClockChange[]): EnrollmentEvent[] {
  if (liveChanges.length === 0) return events;
  return events.map((e) => {
    const t = Date.parse(e.timestamp);
    if (isNaN(t)) return e;
    const cum = cumDeltaAt(e.sequence, liveChanges);
    return cum === 0 ? e : { ...e, timestamp: new Date(t - cum).toISOString() };
  });
}

// ── Sleep episodes ───────────────────────────────────────────────────────────
// system_sleep_episode is emitted retroactively at wake; enteredAt/exitedAt are
// wall-clock payload fields, so they get the same era correction as the event
// that carried them (a clock step DURING the sleep is thereby approximated to
// the post-wake era — acceptable, both boundaries stay consistent).

interface SleepEpisode {
  start: number;
  end: number;
  kind?: string;
  wakeSourceText?: string;
  onAcPower?: boolean;
}

function extractSleepEpisodes(events: EnrollmentEvent[], liveChanges: ClockChange[]): SleepEpisode[] {
  const episodes: SleepEpisode[] = [];
  for (const e of events.filter((x) => x.eventType === 'system_sleep_episode')) {
    const d = (e.data ?? {}) as Record<string, unknown>;
    const cum = cumDeltaAt(e.sequence, liveChanges);
    const start = typeof d.enteredAt === 'string' ? Date.parse(d.enteredAt) - cum : NaN;
    const end = typeof d.exitedAt === 'string' ? Date.parse(d.exitedAt) - cum : NaN;
    if (isNaN(start) || isNaN(end) || !(end > start)) continue;
    episodes.push({
      start,
      end,
      kind: typeof d.kind === 'string' ? d.kind : undefined,
      wakeSourceText:
        typeof d.wakeSourceText === 'string'
          ? d.wakeSourceText
          : typeof d.wakeSourceType === 'string'
            ? d.wakeSourceType
            : undefined,
      onAcPower: typeof d.onAcPower === 'boolean' ? d.onAcPower : undefined,
    });
  }
  return episodes.sort((a, b) => a.start - b.start);
}

/** Carve sleep episodes out of the network segments as 'asleep' segments. */
function overlaySleep(segs: NetSegment[], episodes: SleepEpisode[], t0: number, t1: number): NetSegment[] {
  let out = segs;
  for (const ep of episodes) {
    const s = Math.max(ep.start, t0);
    const e = Math.min(ep.end, t1);
    if (!(e > s)) continue;
    const next: NetSegment[] = [];
    for (const seg of out) {
      if (e <= seg.start || s >= seg.end) {
        next.push(seg);
        continue;
      }
      if (s > seg.start) next.push({ ...seg, end: s });
      if (e < seg.end) next.push({ ...seg, start: e });
    }
    next.push({
      start: s,
      end: e,
      kind: 'asleep',
      identity: 'asleep',
      sleepKind: ep.kind,
      wakeSourceText: ep.wakeSourceText,
      onAcPower: ep.onAcPower,
    });
    next.sort((a, b) => a.start - b.start);
    out = next;
  }
  return out.filter((x) => x.end > x.start);
}

// ── Full model ───────────────────────────────────────────────────────────────

export function buildNetworkModel(session: Session, rawEvents: EnrollmentEvent[]): NetworkModel | null {
  if (rawEvents.length === 0) return null;

  const clockChanges = extractClockChanges(rawEvents);
  const liveChanges = clockChanges.filter((c) => !c.backfilled);
  const events = correctTimestamps(rawEvents, liveChanges);

  const evTimes = events.map((e) => Date.parse(e.timestamp)).filter((t) => !isNaN(t));
  if (evTimes.length === 0) return null;
  const t0 = Math.min(Date.parse(session.startedAt), ...evTimes);
  // completedAt is server wall clock — only usable as the right edge when the
  // axis was not rebased (after a rebase it lives in a different clock era).
  const completedT = session.completedAt && liveChanges.length === 0 ? Date.parse(session.completedAt) : NaN;
  const t1 = Math.max(isNaN(completedT) ? 0 : completedT, ...evTimes);
  if (!(t1 > t0)) return null;

  const sleepEpisodes = extractSleepEpisodes(rawEvents, liveChanges);
  const segments = overlaySleep(buildSegments(events, t0, t1), sleepEpisodes, t0, t1);
  if (segments.length === 0) return null;
  const colors = buildColorMap(segments);
  const scale = buildScale(segments);

  const phaseNames = session.enrollmentType === 'v2' ? V2_PHASE_NAMES : V1_PHASE_NAMES;
  const transitions = events
    .filter((e) => e.eventType === 'phase_transition')
    .map((e) => ({ t: Date.parse(e.timestamp), name: phaseNames[e.phase] ?? e.phaseName ?? `Phase ${e.phase}` }))
    .filter((p) => !isNaN(p.t))
    .sort((a, b) => a.t - b.t);
  const phases: PhaseBand[] = [];
  let prev = { t: t0, name: 'Start' };
  for (const tr of transitions) {
    if (tr.t > prev.t) phases.push({ start: prev.t, end: tr.t, name: prev.name });
    prev = tr;
  }
  phases.push({ start: prev.t, end: t1, name: prev.name });

  const checks: CheckMarker[] = events
    .filter((e) => e.eventType === 'network_connectivity_check')
    .map((e) => {
      const d = (e.data ?? {}) as Record<string, unknown>;
      const results = Array.isArray(d.results) ? (d.results as CheckMarker['results']) : [];
      return {
        t: Date.parse(e.timestamp),
        ok: d.allReachable === true,
        reachable: typeof d.reachableCount === 'number' ? d.reachableCount : results.filter((r) => r.reachable).length,
        total: typeof d.totalCount === 'number' ? d.totalCount : results.length,
        results,
      };
    })
    .filter((c) => !isNaN(c.t));

  const lifeMarkers: LifeMarker[] = events
    .filter((e) =>
      ['system_reboot_detected', 'desktop_arrived', 'enrollment_complete', 'enrollment_failed'].includes(e.eventType),
    )
    .map((e) => ({
      t: Date.parse(e.timestamp),
      label:
        e.eventType === 'system_reboot_detected'
          ? 'Reboot'
          : e.eventType === 'desktop_arrived'
            ? 'Desktop'
            : e.eventType === 'enrollment_complete'
              ? 'Completed'
              : 'Failed',
    }))
    .filter((m) => !isNaN(m.t));

  // Clock-step markers: live steps sit at their corrected changepoint;
  // backfilled steps (pre-agent) use their original event-log time when known.
  for (const c of clockChanges) {
    const t = c.backfilled
      ? (c.timeCreated ? Date.parse(c.timeCreated) : NaN)
      : c.rawT - cumDeltaAt(c.sequence, liveChanges);
    if (isNaN(t) || t < t0 || t > t1) continue;
    const sign = c.deltaMs >= 0 ? '+' : '−';
    const oldT = c.oldTime ? Date.parse(c.oldTime) : NaN;
    const newT = c.newTime ? Date.parse(c.newTime) : NaN;
    const range = !isNaN(oldT) && !isNaN(newT) ? `${fmtTime(oldT)} → ${fmtTime(newT)}` : '';
    lifeMarkers.push({
      t,
      label: `Clock ${sign}${fmtDuration(Math.abs(c.deltaMs))}`,
      detail: [range, c.reason, c.backfilled ? 'before agent start (not rebased)' : 'axis rebased']
        .filter(Boolean)
        .join(' · '),
    });
  }
  lifeMarkers.sort((a, b) => a.t - b.t);

  const networkEvents = events.filter((e) => NETWORK_EVENT_TYPES.has(e.eventType));

  const offlineMs = segments.filter((s) => s.kind === 'offline').reduce((a, s) => a + (s.end - s.start), 0);
  const asleepMs = segments.filter((s) => s.kind === 'asleep').reduce((a, s) => a + (s.end - s.start), 0);
  const distinctNetworks = new Set(
    segments.filter((s) => s.kind === 'wifi' || s.kind === 'ethernet').map((s) => s.identity),
  );
  const switchCount = events.filter((e) => e.eventType === 'network_state_change').length;
  const hotspotDetected = segments.some((s) => s.hotspot);

  return {
    t0,
    t1,
    segments,
    colors,
    scale,
    phases,
    checks,
    lifeMarkers,
    networkEvents,
    offlineMs,
    asleepMs,
    clockChangeCount: clockChanges.length,
    distinctNetworks,
    switchCount,
    hotspotDetected,
  };
}
