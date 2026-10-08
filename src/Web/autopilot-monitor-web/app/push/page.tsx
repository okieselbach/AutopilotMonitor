"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useLatest } from "@/hooks/useLatest";
import { createLongPress } from "@/lib/push/longPress";
import {
  deleteHistoryEntry,
  entryFragment,
  META_KEYS,
  parseFragment,
  pruneStoredHistory,
  readHistory,
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

/**
 * The local history, like a chat log (F12): newest first, kept for the retention chosen on the
 * status page (30 days by default) and capped at 200 entries, written by the service worker and
 * read here. Every open reconciles the device with the server (K14). A long press (or right-click)
 * on an entry opens its actions; a delete stays on the device.
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

  const target = parseFragment(hash);
  const focusId = target?.kind === "entry" ? target.id : null;

  // A new deep link opens its entry; a tap toggles from there (adjust-during-render).
  const [expanded, setExpanded] = useState<{ focusId: string | null; id: string | null }>({ focusId: null, id: null });
  if (expanded.focusId !== focusId) setExpanded({ focusId, id: focusId });

  // Load (prune on open); re-runs whenever the worker, the returning tab or a delete asks for it.
  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      let list: HistoryEntry[] = [];
      try {
        await pruneStoredHistory();
        list = await readHistory();
      } catch {
        // No IndexedDB (private mode): an empty history is the honest state.
      }
      if (cancelled) return;
      setEntries(list);
      setNow(Date.now());
    };
    void load();
    return () => {
      cancelled = true;
    };
  }, [historyKey]);

  // Keep in step with the worker and with the tab coming back.
  useEffect(() => {
    const onMessage = (event: MessageEvent<WorkerMessage>) => {
      if (event.data?.channel !== "am-push") return;
      if (event.data.type === "open" && typeof event.data.id === "string") {
        window.location.hash = entryFragment(event.data.id);
      }
      setHistoryKey((k) => k + 1);
      if (event.data.type === "wiped") setReloadKey((k) => k + 1);
    };
    const onVisible = () => {
      if (document.visibilityState === "visible") {
        setHistoryKey((k) => k + 1);
        setReloadKey((k) => k + 1);
      }
    };
    const worker = "serviceWorker" in navigator ? navigator.serviceWorker : null;
    worker?.addEventListener("message", onMessage);
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      worker?.removeEventListener("message", onMessage);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, []);

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

  return (
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

      {entries && entries.length > 0 && (
        <ul className="space-y-2">
          {entries.map((entry) => (
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
