"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { HISTORY_MAX_ENTRIES, META_KEYS, readRetentionDays, writeRetentionDays } from "@/lib/push/pushCore";
import { describePushError } from "@/lib/push/pushApi";
import { isStoragePersisted, reconcileDevice, unpairDevice, type ReconcileResult } from "@/lib/push/pushClient";
import { describeDeviceStatus, formatDateTime } from "@/lib/push/pushFormat";
import { describeRetention, parseRetentionInput } from "@/lib/push/retentionInput";
import { useIsClient, useNotificationPermission } from "../pushEnvironment";
import { DEVICE_STATUS_CHIP, NEUTRAL_CHIP } from "../pushStyles";

type UnpairState = { kind: "idle" } | { kind: "confirm" } | { kind: "working" } | { kind: "done" } | { kind: "error"; message: string };

/** The retention field's feedback line; "saved" carries its time so a second save restarts the timer. */
type RetentionNote = { kind: "idle" } | { kind: "working" } | { kind: "saved"; at: number } | { kind: "error"; message: string };

/** How long "Saved." stays under the retention field. */
const SAVED_NOTE_MS = 2000;

/** Device status as the server sees it, plus the local facts that decide whether a push can arrive. */
export default function PushStatusPage() {
  const isClient = useIsClient();
  const permission = useNotificationPermission();
  const [result, setResult] = useState<ReconcileResult | null>(null);
  const [persisted, setPersisted] = useState<boolean | null>(null);
  const [unpair, setUnpair] = useState<UnpairState>({ kind: "idle" });
  // The stored retention (null: not readable), what is typed (null: show the stored value), the feedback line.
  const [retention, setRetention] = useState<number | null>(null);
  const [retentionInput, setRetentionInput] = useState<string | null>(null);
  const [retentionNote, setRetentionNote] = useState<RetentionNote>({ kind: "idle" });

  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      const [reconciled, storage, days] = await Promise.all([
        reconcileDevice(),
        isStoragePersisted(),
        readRetentionDays().catch(() => null),
      ]);
      if (cancelled) return;
      setResult(reconciled);
      setPersisted(storage);
      setRetention(days);
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, []);

  // "Saved." fades after a moment; typing or another save replaces it, and the cleanup drops the timer.
  const savedAt = retentionNote.kind === "saved" ? retentionNote.at : null;
  useEffect(() => {
    if (savedAt === null) return;
    const timer = setTimeout(() => setRetentionNote({ kind: "idle" }), SAVED_NOTE_MS);
    return () => clearTimeout(timer);
  }, [savedAt]);

  const retentionText = retentionInput ?? (retention === null ? "" : String(retention));

  const saveRetention = async () => {
    const parsed = parseRetentionInput(retentionText);
    if (!parsed.ok) {
      setRetentionNote({ kind: "error", message: parsed.message });
      return;
    }
    setRetentionNote({ kind: "working" });
    try {
      // Stores and prunes at once; the stored value is what the field shows from now on.
      const stored = await writeRetentionDays(parsed.days);
      setRetention(stored);
      setRetentionInput(null);
      setRetentionNote({ kind: "saved", at: Date.now() });
    } catch {
      setRetentionNote({ kind: "error", message: "Could not save on this device." });
    }
  };

  const doUnpair = async () => {
    setUnpair({ kind: "working" });
    try {
      await unpairDevice();
      setUnpair({ kind: "done" });
      setResult({ state: "unpaired" });
    } catch (error) {
      setUnpair({ kind: "error", message: describePushError(error) });
    }
  };

  if (!isClient || result === null) {
    return <p className="text-sm text-gray-500">Loading…</p>;
  }

  const device = result.state === "ok" ? result.device : null;
  const meta = result.state === "ok" || result.state === "error" ? result.meta : null;
  const paired = result.state === "ok" || result.state === "error";

  return (
    <div className="space-y-4">
      <section className="bg-white rounded-lg shadow p-5 space-y-3">
        <div className="flex items-center justify-between gap-2">
          <h1 className="text-lg font-semibold text-gray-900">This device</h1>
          {device && (
            <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${DEVICE_STATUS_CHIP[device.status] ?? NEUTRAL_CHIP}`}>
              {device.status}
            </span>
          )}
        </div>

        {result.state === "unpaired" && (
          <p className="text-sm text-gray-600">
            {unpair.kind === "done" ? "This device was unpaired." : "This device is not paired."}
          </p>
        )}
        {result.state === "gone" && <p className="text-sm text-gray-600">This device is no longer paired. The server no longer knows it.</p>}
        {result.state === "error" && (
          <p className="text-sm text-gray-600">
            The server could not be reached: {result.message}
          </p>
        )}
        {device && <p className="text-sm text-gray-600">{describeDeviceStatus(device.status)}</p>}

        {paired && (
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
            <Row name="Name" value={device?.label ?? meta?.[META_KEYS.label] ?? "—"} />
            <Row name="Platform" value={device?.platform ?? meta?.[META_KEYS.platform] ?? "—"} />
            {device && (
              <>
                <Row name="Scope" value={device.scopeName} />
                <Row name="Owner" value={device.ownerUpn} />
                <Row name="Paired" value={formatDateTime(device.pairedUtc)} />
                <Row name="Confirmed" value={formatDateTime(device.confirmedUtc)} />
                <Row name="Last delivered" value={formatDateTime(device.lastDeliveredUtc)} />
                <Row
                  name="Server key"
                  value={!device.activeKid || device.activeKid === meta?.[META_KEYS.kid] ? "current" : "outdated – migrates on the next open"}
                />
              </>
            )}
          </dl>
        )}

        {!paired && (
          <Link href="/push/pair" className="inline-block text-sm font-medium text-green-700 hover:underline">
            Pair this device
          </Link>
        )}
      </section>

      <section className="bg-white rounded-lg shadow p-5 space-y-3">
        <h2 className="text-base font-semibold text-gray-900">On this device</h2>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
          <Row name="Notifications" value={permission === "unsupported" ? "not supported" : permission} />
          <Row name="Subscription" value={result.state === "ok" ? (result.subscriptionPresent ? "present" : "missing") : "—"} />
          <Row name="Persistent storage" value={persisted === null ? "unknown" : persisted ? "granted" : "not granted"} />
        </dl>
        {result.state === "ok" && !result.subscriptionPresent && permission !== "granted" && (
          <p className="text-xs text-gray-500">Without notification permission the browser holds no push subscription; alerts cannot arrive.</p>
        )}

        {paired && (
          <div className="pt-3 border-t border-gray-100 space-y-2">
            <div className="flex items-baseline justify-between gap-2">
              <h3 className="text-sm font-medium text-gray-900">History retention</h3>
              <span className="text-sm text-gray-600">{retention === null ? "—" : describeRetention(retention)}</span>
            </div>
            <form
              noValidate
              onSubmit={(e) => {
                e.preventDefault();
                void saveRetention();
              }}
              className="flex items-end gap-2"
            >
              <label className="block flex-1 min-w-0">
                <span className="text-xs font-medium text-gray-700">Days to keep</span>
                <input
                  type="text"
                  inputMode="numeric"
                  pattern="[0-9]*"
                  autoComplete="off"
                  value={retentionText}
                  onChange={(e) => {
                    setRetentionInput(e.target.value);
                    setRetentionNote({ kind: "idle" });
                  }}
                  disabled={retentionNote.kind === "working"}
                  aria-describedby={retentionNote.kind === "error" ? "retention-help retention-error" : "retention-help"}
                  aria-invalid={retentionNote.kind === "error"}
                  className="mt-1 w-full px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
                />
              </label>
              <button
                type="submit"
                disabled={retentionNote.kind === "working"}
                className="px-4 py-2 bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors text-sm font-medium disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {retentionNote.kind === "working" ? "Saving…" : "Save"}
              </button>
            </form>
            <p id="retention-help" className="text-xs text-gray-500">
              0 keeps entries until the cap of {HISTORY_MAX_ENTRIES}; the newest {HISTORY_MAX_ENTRIES} are kept in any case.
            </p>
            {retentionNote.kind === "error" && (
              <p id="retention-error" className="text-sm text-red-700 dark:text-red-400">
                {retentionNote.message}
              </p>
            )}
            {retentionNote.kind === "saved" && (
              <p role="status" className="text-sm text-green-700">
                Saved.
              </p>
            )}
          </div>
        )}
      </section>

      {paired && (
        <section className="bg-white rounded-lg shadow p-5 space-y-3">
          <h2 className="text-base font-semibold text-gray-900">Unpair</h2>
          <p className="text-sm text-gray-600">Removes this device from your account and clears the local history.</p>
          {unpair.kind === "error" && <p className="text-sm text-red-700 dark:text-red-400">{unpair.message}</p>}
          <div className="flex flex-wrap items-center gap-3">
            {unpair.kind === "confirm" ? (
              <>
                <button
                  type="button"
                  onClick={() => void doUnpair()}
                  className="px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700 transition-colors text-sm font-medium"
                >
                  Yes, unpair this device
                </button>
                <button
                  type="button"
                  onClick={() => setUnpair({ kind: "idle" })}
                  className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900"
                >
                  Keep it
                </button>
              </>
            ) : (
              <button
                type="button"
                onClick={() => setUnpair({ kind: "confirm" })}
                disabled={unpair.kind === "working"}
                className="px-4 py-2 bg-white border border-red-300 text-red-700 rounded-lg hover:bg-red-50 transition-colors text-sm font-medium disabled:opacity-50"
              >
                {unpair.kind === "working" ? "Unpairing…" : "Unpair this device"}
              </button>
            )}
            <Link href="/push/pair" className="text-sm font-medium text-gray-600 hover:text-gray-900">
              Re-pair
            </Link>
          </div>
        </section>
      )}
    </div>
  );
}

function Row({ name, value }: { name: string; value: string }) {
  return (
    <>
      <dt className="text-gray-500">{name}</dt>
      <dd className="text-gray-900 break-words min-w-0">{value}</dd>
    </>
  );
}
