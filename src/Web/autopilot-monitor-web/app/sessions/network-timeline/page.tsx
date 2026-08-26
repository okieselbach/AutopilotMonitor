'use client';

// ─────────────────────────────────────────────────────────────────────────────
// Network timeline.
//
// Standalone full-analysis view of the network timeline for one session:
// stats row, the NetworkBand chart, and the raw network event list. Reached
// via the "Network timeline" deep link in the session detail page's
// DeviceDetailsCard (Network section).
//
// Open at: /sessions/network-timeline?id=<sessionId>&tenantId=<tenantId>
// ─────────────────────────────────────────────────────────────────────────────

import { Suspense, useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'next/navigation';
import Link from 'next/link';
import { ProtectedRoute } from '@/components/ProtectedRoute';
import { useAuth } from '@/contexts/AuthContext';
import { api } from '@/lib/api';
import { authenticatedFetch } from '@/lib/authenticatedFetch';
import { extractContinuation, MAX_EAGER_PAGES } from '@/lib/paginationLink';
import { sessionUrl } from '@/lib/routes';
import NetworkBand from './NetworkBand';
import { buildNetworkModel, fmtDuration, NetworkModel } from './networkTimelineModel';
import type { EnrollmentEvent, Session } from '@/types';

// ── Data loading ─────────────────────────────────────────────────────────────

function useSessionData(sessionId: string | null, tenantIdOverride: string | undefined) {
  const { getAccessToken } = useAuth();
  const [session, setSession] = useState<Session | null>(null);
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!sessionId) return;
    setLoading(true);
    setError(null);
    try {
      const sResp = await authenticatedFetch(api.sessions.get(sessionId, tenantIdOverride), getAccessToken);
      if (!sResp.ok) throw new Error(`Failed to load session (${sResp.status})`);
      const sData = await sResp.json();
      const found: Session | undefined =
        sData.session ?? sData.sessions?.find((s: Session) => s.sessionId === sessionId);
      if (!found) throw new Error('Session not found');
      setSession(found);

      const tenantId = tenantIdOverride ?? found.tenantId;
      const all: EnrollmentEvent[] = [];
      let url = api.sessions.events(sessionId, tenantId, { pageSize: 200 });
      for (let page = 0; page < MAX_EAGER_PAGES; page++) {
        const resp = await authenticatedFetch(url, getAccessToken);
        if (!resp.ok) throw new Error(`Failed to load events (${resp.status})`);
        const data = await resp.json();
        if (Array.isArray(data.events)) all.push(...data.events);
        const cont = extractContinuation(data.nextLink);
        if (!cont) break;
        url = api.sessions.events(sessionId, tenantId, { pageSize: 200, continuation: cont });
      }
      all.sort((a, b) => a.sequence - b.sequence);
      setEvents(all);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [sessionId, tenantIdOverride, getAccessToken]);

  useEffect(() => {
    const run = async () => {
      await load();
    };
    void run();
  }, [load]);

  return { session, events, loading, error };
}

// ── Page ─────────────────────────────────────────────────────────────────────

export default function NetworkTimelinePage() {
  return (
    <ProtectedRoute>
      <Suspense fallback={<div className="p-8 text-gray-500">Loading…</div>}>
        <Content />
      </Suspense>
    </ProtectedRoute>
  );
}

function Content() {
  const searchParams = useSearchParams();
  const sessionId = searchParams?.get('id') ?? null;
  const tenantIdOverride = searchParams?.get('tenantId') ?? undefined;

  const { session, events, loading, error } = useSessionData(sessionId, tenantIdOverride);
  const model = useMemo(() => (session ? buildNetworkModel(session, events) : null), [session, events]);

  if (!sessionId) {
    return (
      <div className="max-w-2xl mx-auto p-8">
        <h1 className="text-2xl font-bold mb-2 text-gray-900">Network Timeline</h1>
        <p className="text-gray-600 mb-4">
          Provide the session ID via query parameters:{' '}
          <code className="bg-gray-100 px-1 rounded">/sessions/network-timeline?id=&lt;sessionId&gt;&amp;tenantId=&lt;tenantId&gt;</code>
        </p>
      </div>
    );
  }

  return (
    <div className="max-w-7xl mx-auto p-4 sm:p-8">
      <h1 className="text-2xl font-bold text-gray-900 mb-1">Network Timeline</h1>

      {session && (
        <p className="text-sm text-gray-600 mb-6">
          {session.deviceName} · {session.model} · SN {session.serialNumber} ·{' '}
          <span className="font-medium">{session.status}</span> ·{' '}
          <Link
            href={sessionUrl(session.sessionId, { tenantId: session.tenantId })}
            className="text-blue-600 hover:underline"
          >
            Open session detail
          </Link>
        </p>
      )}

      {loading && <div className="text-gray-500 py-12 text-center">Loading session data…</div>}
      {error && <div className="text-red-600 py-6">{error}</div>}

      {model && session && (
        <>
          <StatsRow model={model} />
          <div className="bg-white border border-gray-200 rounded-lg p-4 mb-6">
            <NetworkBand model={model} />
            {model.hotspotDetected && (
              <p className="mt-1 text-[11px] text-amber-700">
                Hotspot classification is heuristic: &quot;Hotspot?&quot; marks subnet-only evidence (e.g. 172.20.10.x
                on iOS); without the &quot;?&quot; the SSID itself matches a typical phone hotspot name.
              </p>
            )}
          </div>
          <EventList events={model.networkEvents} />
        </>
      )}
    </div>
  );
}

// ── Stats row ────────────────────────────────────────────────────────────────

function StatsRow({ model }: { model: NetworkModel }) {
  const wifiSegs = model.segments.filter((s) => s.kind === 'wifi' && s.signalPercent != null);
  const avgSignal = wifiSegs.length
    ? Math.round(wifiSegs.reduce((a, s) => a + (s.signalPercent ?? 0), 0) / wifiSegs.length)
    : null;
  const lastCheck = model.checks[model.checks.length - 1];
  const items: { label: string; value: string; tone?: 'bad' | 'good' }[] = [
    { label: 'Duration', value: fmtDuration(model.t1 - model.t0) },
    { label: 'Networks', value: String(model.distinctNetworks.size) },
    { label: 'Network change events', value: String(model.switchCount) },
    {
      label: 'Offline total',
      value: model.offlineMs > 0 ? fmtDuration(model.offlineMs) : '—',
      tone: model.offlineMs > 0 ? 'bad' : undefined,
    },
  ];
  if (model.asleepMs > 0) items.push({ label: 'Asleep total', value: fmtDuration(model.asleepMs) });
  if (model.clockChangeCount > 0) items.push({ label: 'Clock changes', value: String(model.clockChangeCount) });
  if (avgSignal != null) {
    items.push({ label: 'Avg WiFi signal', value: `${avgSignal}%` });
  } else if (model.segments.some((s) => s.dataLimitedReason === 'location_services_off')) {
    // Say why the stat is empty. Otherwise a fleet-wide blank reads as a product defect
    // rather than the Windows 24H2 location gate that actually caused it.
    items.push({ label: 'Avg WiFi signal', value: 'n/a (Location services off)' });
  }
  if (lastCheck)
    items.push({
      label: 'Last connectivity check',
      value: `${lastCheck.reachable}/${lastCheck.total} reachable`,
      tone: lastCheck.ok ? 'good' : 'bad',
    });

  return (
    <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3 mb-6">
      {items.map((it) => (
        <div key={it.label} className="bg-white border border-gray-200 rounded-lg px-3 py-2">
          <div className="text-[11px] uppercase tracking-wide text-gray-500">{it.label}</div>
          <div
            className={`text-lg font-semibold ${
              it.tone === 'bad' ? 'text-red-600' : it.tone === 'good' ? 'text-green-600' : 'text-gray-900'
            }`}
          >
            {it.value}
          </div>
        </div>
      ))}
    </div>
  );
}

// ── Event list ───────────────────────────────────────────────────────────────

const CHANGE_TYPE_BADGES: Record<string, string> = {
  network_lost: 'bg-red-100 text-red-800',
  network_restored: 'bg-green-100 text-green-800',
  type_change: 'bg-amber-100 text-amber-800',
  ssid_change: 'bg-violet-100 text-violet-800',
  adapter_change: 'bg-cyan-100 text-cyan-800',
  ip_change: 'bg-gray-100 text-gray-800',
};

function EventList({ events }: { events: EnrollmentEvent[] }) {
  if (events.length === 0) {
    return <div className="text-gray-500 py-6">No network or system events in this session.</div>;
  }
  return (
    <div className="bg-white border border-gray-200 rounded-lg overflow-hidden">
      <div className="px-4 py-3 border-b border-gray-200 font-semibold text-gray-900">
        Network &amp; system events ({events.length})
      </div>
      <ul className="divide-y divide-gray-100">
        {events.map((e) => {
          const d = (e.data ?? {}) as Record<string, unknown>;
          const changeType = typeof d.changeType === 'string' ? d.changeType : null;
          const rest = Object.fromEntries(Object.entries(d).filter(([k]) => k !== 'decisionState'));
          return (
            <li key={e.eventId || `${e.sessionId}-${e.sequence}`} className="px-4 py-2.5">
              <details>
                <summary className="cursor-pointer flex flex-wrap items-center gap-2 text-sm">
                  <span className="font-mono text-xs text-gray-500 w-20 shrink-0">
                    {new Date(e.timestamp).toLocaleTimeString(undefined, { hour12: false })}
                  </span>
                  <span className="text-xs px-1.5 py-0.5 rounded bg-blue-50 text-blue-700 font-mono">{e.eventType}</span>
                  {changeType && (
                    <span
                      className={`text-xs px-1.5 py-0.5 rounded font-medium ${CHANGE_TYPE_BADGES[changeType] ?? 'bg-gray-100 text-gray-700'}`}
                    >
                      {changeType}
                    </span>
                  )}
                  <span className="text-gray-800">{e.message}</span>
                </summary>
                <pre className="mt-2 text-xs bg-gray-50 rounded p-3 overflow-x-auto text-gray-700">
                  {JSON.stringify(rest, null, 2)}
                </pre>
              </details>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
