"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { useLatest } from "@/hooks/useLatest";
import { ALL_FILTER, applyHistoryFilter, buildFilterOptions, hasFilterOption, isFilterUseful, normalizeFilterKey } from "@/lib/push/historyFilter";
import { sameHistory } from "@/lib/push/historyList";
import { createLongPress } from "@/lib/push/longPress";
import { createPullToRefresh, type PullState } from "@/lib/push/pullToRefresh";
import {
  deleteHistoryEntry,
  entryFragment,
  META_KEYS,
  parseFragment,
  pruneStoredHistory,
  readHistory,
  readMeta,
  writeMeta,
  type HistoryEntry,
} from "@/lib/push/pushCore";
import { readPairCookie, reconcileDevice, type ReconcileResult } from "@/lib/push/pushClient";
import { formatDateTime, formatRelativeTime } from "@/lib/push/pushFormat";
import { EntryActionSheet } from "./EntryActionSheet";
import { useIsClient, useLocationHash } from "./pushEnvironment";
import { DANGER_CHIP, DEVICE_STATUS_CHIP, NEUTRAL_CHIP, SEVERITY_ACCENT } from "./pushStyles";

interface WorkerMessage {
  channel?: unknown;
  type?: unknown;
  id?: unknown;
}

/** The history is re-read from IndexedDB this often while the page is visible — cheap, local, and the one path that needs no message from the worker. */
const HISTORY_POLL_MS = 5000;
/** A refresh shows its indicator at least this long, so a fast read still reads as "done". */
const REFRESH_MIN_MS = 500;

/**
 * The local history, like a chat log (F12): newest first, kept for the retention chosen on the
 * status page (30 days by default) and capped at 200 entries, written by the service worker and
 * read here. New entries show up by themselves: the worker's message, a poll every few seconds
 * while visible, and the tab/app coming back all re-read the store; a pull from the top refreshes
 * on demand (a home-screen app has no native gesture). Every open reconciles the device with the
 * server (K14). A long press (or right-click) on an entry opens its actions; a delete stays on
 * the device.
 */
export default function PushHistoryPage() {
  const router = useRouter();
  const isClient = useIsClient();
  const hash = useLocationHash();
  const [entries, setEntries] = useState<HistoryEntry[] | null>(null);
  const [device, setDevice] = useState<ReconcileResult | null>(null);
  const [historyKey, setHistoryKey] = useState(0);
  const [reloadKey, setReloadKey] = useState(0);
  const [now, setNow] = useState(() => 0);
  const [sheet, setSheet] = useState<HistoryEntry | null>(null);
  const [pull, setPull] = useState<PullState>({ pulling: false, distance: 0, armed: false, refreshing: false });
  const [filter, setFilter] = useState(ALL_FILTER);

  const target = parseFragment(hash);
  const focusId = target?.kind === "entry" ? target.id : null;

  // A new deep link opens its entry; a tap toggles from there (adjust-during-render).
  const [expanded, setExpanded] = useState<{ focusId: string | null; id: string | null }>({ focusId: null, id: null });
  if (expanded.focusId !== focusId) setExpanded({ focusId, id: focusId });

  // One read of the store (prune first). An unchanged list keeps its identity so a poll never re-renders for nothing.
  const loadHistory = useCallback(async () => {
    let list: HistoryEntry[] = [];
    try {
      await pruneStoredHistory();
      list = await readHistory();
    } catch {
      // No IndexedDB (private mode): an empty history is the honest state.
    }
    setEntries((prev) => (sameHistory(prev, list) ? prev : list));
    setNow(Date.now());
  }, []);

  // Load on open; re-runs whenever the worker, the returning tab, a delete or a pull asks for it.
  useEffect(() => {
    const run = async () => {
      await loadHistory();
    };
    void run();
  }, [historyKey, loadHistory]);

  // Keep in step with the worker, with the tab or app coming back, and — while visible — with a poll.
  useEffect(() => {
    const onMessage = (event: MessageEvent<WorkerMessage>) => {
      if (event.data?.channel !== "am-push") return;
      if (event.data.type === "open" && typeof event.data.id === "string") {
        window.location.hash = entryFragment(event.data.id);
      }
      setHistoryKey((k) => k + 1);
      if (event.data.type === "wiped") setReloadKey((k) => k + 1);
    };
    const onBack = () => {
      if (document.visibilityState !== "visible") return;
      setHistoryKey((k) => k + 1);
      setReloadKey((k) => k + 1);
    };
    const poll = window.setInterval(() => {
      if (document.visibilityState === "visible") void loadHistory();
    }, HISTORY_POLL_MS);
    const worker = "serviceWorker" in navigator ? navigator.serviceWorker : null;
    worker?.addEventListener("message", onMessage);
    document.addEventListener("visibilitychange", onBack);
    window.addEventListener("focus", onBack);
    window.addEventListener("pageshow", onBack);
    return () => {
      window.clearInterval(poll);
      worker?.removeEventListener("message", onMessage);
      document.removeEventListener("visibilitychange", onBack);
      window.removeEventListener("focus", onBack);
      window.removeEventListener("pageshow", onBack);
    };
  }, [loadHistory]);

  // Pull from the top to refresh: the history is re-read and the device reconciled; the
  // indicator stays visible for a moment so a fast read still reads as "done".
  const refresh = useLatest(async () => {
    const started = Date.now();
    setReloadKey((k) => k + 1);
    await loadHistory();
    const remaining = REFRESH_MIN_MS - (Date.now() - started);
    if (remaining > 0) await new Promise((resolve) => setTimeout(resolve, remaining));
  });
  const [puller] = useState(() =>
    createPullToRefresh({
      getScrollTop: () => window.scrollY,
      onRefresh: () => refresh.current(),
      onChange: setPull,
    }),
  );
  useEffect(() => () => puller.dispose(), [puller]);

  // The filter is a per-device preference, stored next to the retention; a stale key keeps everything.
  useEffect(() => {
    const run = async () => {
      try {
        setFilter(normalizeFilterKey((await readMeta())[META_KEYS.historyFilter]));
      } catch {
        // No IndexedDB: the default "all" stands.
      }
    };
    void run();
  }, []);
  const chooseFilter = (key: string) => {
    setFilter(key);
    writeMeta({ [META_KEYS.historyFilter]: key }).catch(() => {});
  };

  const groups = buildFilterOptions(entries ?? []);
  const filterShown = isFilterUseful(groups);
  const selected = hasFilterOption(groups, filter) ? filter : ALL_FILTER;
  const visible = entries ? applyHistoryFilter(entries, selected, groups) : null;

  // A deep link to an entry the filter hides shows everything (adjust-during-render, once per link).
  const [filterAdjust, setFilterAdjust] = useState<string | null>(null);
  if (
    focusId &&
    filterAdjust !== focusId &&
    entries &&
    visible &&
    selected !== ALL_FILTER &&
    entries.some((e) => e.id === focusId) &&
    !visible.some((e) => e.id === focusId)
  ) {
    setFilterAdjust(focusId);
    chooseFilter(ALL_FILTER);
  }

  // Reconcile with the server; an unpaired device holding a Safari hand-off cookie continues to pairing.
  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      const result = await reconcileDevice();
      if (cancelled) return;
      if (result.state === "unpaired" && readPairCookie()) {
        router.replace("/push/pair");
        return;
      }
      setDevice(result);
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [reloadKey, router]);

  // Bring the deep-linked entry into view once it is rendered.
  useEffect(() => {
    if (!focusId || !entries) return;
    document.getElementById(`entry-${focusId}`)?.scrollIntoView({ block: "center" });
  }, [focusId, entries]);

  const toggle = (id: string) => setExpanded((prev) => ({ ...prev, id: prev.id === id ? null : id }));

  // The delete never leaves the device. IndexedDB may refuse (private mode): the entry just stays.
  const deleteEntry = async (entry: HistoryEntry) => {
    setSheet(null);
    try {
      await deleteHistoryEntry(entry.id);
    } catch {
      return;
    }
    setExpanded((prev) => (prev.id === entry.id ? { ...prev, id: null } : prev));
    if (focusId === entry.id) window.location.hash = "";
    setHistoryKey((k) => k + 1);
  };

  const pullLabel = pull.refreshing ? "Refreshing…" : pull.armed ? "Release to refresh" : "Pull to refresh";

  return (
    <div onTouchStart={puller.onTouchStart} onTouchMove={puller.onTouchMove} onTouchEnd={puller.onTouchEnd} onTouchCancel={puller.onTouchCancel}>
      <div
        className="overflow-hidden flex items-end justify-center text-xs text-gray-500"
        style={{ height: pull.distance, transition: pull.pulling ? "none" : "height 150ms ease-out" }}
        aria-live="polite"
      >
        {pull.distance > 0 && (
          <span className="pb-2 inline-flex items-center gap-1.5">
            {pull.refreshing ? (
              <span className="inline-block w-3.5 h-3.5 border-2 border-gray-300 border-t-gray-600 rounded-full animate-spin" aria-hidden="true" />
            ) : (
              <span className="inline-block transition-transform" style={{ transform: pull.armed ? "rotate(180deg)" : "none" }} aria-hidden="true">
                ↓
              </span>
            )}
            {pullLabel}
          </span>
        )}
      </div>

      <div className="space-y-4">
        <DeviceChip device={device} />

        {device?.state === "gone" && (
          <div className="bg-white rounded-lg shadow p-4 border-l-4 border-red-500">
            <p className="text-sm font-medium text-gray-900">This device is no longer paired.</p>
            <p className="text-sm text-gray-600 mt-1">The alerts stop here until you pair it again from the portal.</p>
            <Link href="/push/pair" className="inline-block mt-3 text-sm font-medium text-green-700 hover:underline">
              Pair this device again
            </Link>
          </div>
        )}

        {filterShown && entries && visible && (
          <div className="flex items-center gap-2 text-sm">
            <label htmlFor="history-filter" className="sr-only">
              Filter alerts
            </label>
            <select
              id="history-filter"
              value={selected}
              onChange={(event) => chooseFilter(event.target.value)}
              className="flex-1 min-w-0 bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm text-gray-900"
            >
              <option value={ALL_FILTER}>All alerts ({entries.length})</option>
              {groups.map((group) => (
                <optgroup key={group.group} label={group.label}>
                  {group.options.map((option) => (
                    <option key={option.key} value={option.key}>
                      {option.label} ({option.count})
                    </option>
                  ))}
                </optgroup>
              ))}
            </select>
            {selected !== ALL_FILTER && (
              <>
                <span className="text-xs text-gray-500 shrink-0">
                  {visible.length} of {entries.length}
                </span>
                <button
                  type="button"
                  onClick={() => chooseFilter(ALL_FILTER)}
                  aria-label="Clear filter"
                  title="Show all alerts"
                  className="shrink-0 w-8 h-8 inline-flex items-center justify-center rounded-lg border border-gray-300 bg-white text-gray-600 hover:text-gray-900"
                >
                  ×
                </button>
              </>
            )}
          </div>
        )}

        {entries && visible && entries.length > 0 && visible.length === 0 && (
          <div className="bg-white rounded-lg shadow p-6 text-center">
            <p className="text-sm text-gray-600">No alerts match this filter.</p>
            <button type="button" onClick={() => chooseFilter(ALL_FILTER)} className="mt-2 text-sm font-medium text-green-700 hover:underline">
              Show all alerts
            </button>
          </div>
        )}

        {isClient && entries !== null && entries.length === 0 && (
          <div className="bg-white rounded-lg shadow p-6 text-center">
            <p className="text-sm text-gray-600">No alerts yet.</p>
            {device?.state === "unpaired" ? (
              <p className="text-sm text-gray-600 mt-2">
                Pair this device from the portal to receive alerts here.{" "}
                <Link href="/push/pair" className="font-medium text-green-700 hover:underline">
                  Pair this device
                </Link>
              </p>
            ) : (
              <p className="text-sm text-gray-500 mt-2">New alerts appear here as they arrive.</p>
            )}
          </div>
        )}

        {visible && visible.length > 0 && (
          <ul className="space-y-2">
            {visible.map((entry) => (
              <EntryCard
                key={entry.id}
                entry={entry}
                open={expanded.id === entry.id}
                focused={focusId === entry.id}
                now={now}
                onToggle={toggle}
                onActions={setSheet}
              />
            ))}
          </ul>
        )}

        {sheet && <EntryActionSheet entry={sheet} onDelete={() => void deleteEntry(sheet)} onClose={() => setSheet(null)} />}
      </div>
    </div>
  );
}

interface EntryCardProps {
  entry: HistoryEntry;
  open: boolean;
  focused: boolean;
  now: number;
  onToggle: (id: string) => void;
  onActions: (entry: HistoryEntry) => void;
}

/**
 * One history entry. A tap expands it; a long press or a right-click opens its actions. The
 * card suppresses the browser's own text selection and the iOS callout so the press reads as
 * a press, and it ignores the click that the release after a long press may produce.
 */
function EntryCard({ entry, open, focused, now, onToggle, onActions }: EntryCardProps) {
  const openActions = useLatest(() => onActions(entry));
  const [press] = useState(() => createLongPress({ onLongPress: () => openActions.current() }));
  useEffect(() => () => press.dispose(), [press]);

  return (
    <li
      id={`entry-${entry.id}`}
      className={`bg-white rounded-lg shadow border-l-4 select-none ${SEVERITY_ACCENT[entry.severity]} ${
        focused ? "ring-2 ring-sky-300 dark:ring-sky-700" : ""
      }`}
      style={{ WebkitTouchCallout: "none" }}
      onPointerDown={press.onPointerDown}
      onPointerMove={press.onPointerMove}
      onPointerUp={press.onPointerUp}
      onPointerCancel={press.onPointerCancel}
      onPointerLeave={press.onPointerLeave}
      onContextMenu={(event) => {
        event.preventDefault();
        onActions(entry);
      }}
    >
      <button
        type="button"
        onClick={() => {
          if (!press.shouldSuppressClick()) onToggle(entry.id);
        }}
        aria-expanded={open}
        className="w-full text-left px-3 py-2.5"
      >
        <div className="flex items-baseline justify-between gap-2">
          <span className="text-sm font-medium text-gray-900 break-words min-w-0">{entry.title}</span>
          <time dateTime={entry.ts} title={formatDateTime(entry.ts)} className="text-xs text-gray-500 shrink-0">
            {now ? formatRelativeTime(entry.ts, now) : ""}
          </time>
        </div>
        {entry.body && (
          <p className={`text-sm text-gray-600 mt-0.5 break-words ${open ? "" : "line-clamp-2"}`}>{entry.body}</p>
        )}
      </button>
      {open && (
        <div className="px-3 pb-3 space-y-2">
          {entry.facts.length > 0 && (
            <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
              {entry.facts.map((fact) => (
                <FactRow key={`${fact.name}:${fact.value}`} name={fact.name} value={fact.value} />
              ))}
            </dl>
          )}
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-gray-500">
            <span>{entry.type}</span>
            <span>{entry.scope}</span>
            <span>{formatDateTime(entry.ts)}</span>
          </div>
          {entry.portalUrl && (
            <a
              href={entry.portalUrl}
              target="_blank"
              rel="noopener noreferrer"
              onClick={(event) => {
                if (press.shouldSuppressClick()) event.preventDefault();
              }}
              className="inline-block text-sm font-medium text-sky-700 dark:text-sky-400 hover:underline"
            >
              Open in the portal
            </a>
          )}
        </div>
      )}
    </li>
  );
}

function FactRow({ name, value }: { name: string; value: string }) {
  return (
    <>
      <dt className="text-gray-500">{name}</dt>
      <dd className="text-gray-800 break-words min-w-0">{value}</dd>
    </>
  );
}

function DeviceChip({ device }: { device: ReconcileResult | null }) {
  if (!device) return <div className="h-6" aria-hidden="true" />;
  let chip = NEUTRAL_CHIP;
  let text = "Not paired";
  if (device.state === "ok") {
    chip = DEVICE_STATUS_CHIP[device.device.status] ?? NEUTRAL_CHIP;
    text = `${device.device.status} · ${device.device.label}`;
  } else if (device.state === "gone") {
    chip = DANGER_CHIP;
    text = "Unpaired";
  } else if (device.state === "error") {
    chip = NEUTRAL_CHIP;
    text = device.meta[META_KEYS.label] ? `Offline · ${device.meta[META_KEYS.label]}` : "Offline";
  }
  return (
    <div className="flex items-center justify-between gap-2">
      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${chip}`}>{text}</span>
      <Link href="/push/status" className="text-xs text-gray-500 hover:text-gray-700">
        Details
      </Link>
    </div>
  );
}
