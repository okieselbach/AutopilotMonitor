"use client";

import { useCallback, useEffect, useState } from "react";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { PairDeviceDialog } from "@/components/push/PairDeviceDialog";
import { useAuth } from "@/contexts/AuthContext";
import { useNotifications } from "@/contexts/NotificationContext";
import { apiErrorText } from "@/lib/apiClient";
import { LABEL_MAX_CHARS } from "@/lib/push/pushCore";
import { formatRelativeTime } from "@/lib/push/pushFormat";
import {
  describePortalDeviceStatus,
  hasScopeDevice,
  platformLabel,
  statusTone,
  type PushScope,
  type StatusTone,
} from "@/lib/pushPortal";
import { listPushDevices, removePushDevice, renamePushDevice, testPushDevice } from "@/lib/pushPortalApi";
import type { PairingDeviceDto, PushDeviceDto } from "@/lib/generated/wire-types.generated";

/** Status chip colours: green = delivering, orange = unclear, gray = idle (no new families). */
const CHIP_CLASSES: Record<StatusTone, string> = {
  green: "bg-green-100 text-green-800",
  orange: "bg-orange-100 text-orange-800",
  gray: "bg-gray-100 text-gray-700",
};

/** Heroicons outline "device-phone-mobile". */
const PHONE_ICON = "M12 18h.01M8 21h8a2 2 0 002-2V5a2 2 0 00-2-2H8a2 2 0 00-2 2v14a2 2 0 002 2z";

const TOAST_KEY = "push-devices";

interface PushDevicesPanelProps {
  /** `tenant` talks to push/…, `platform` to global/push/… — and picks the host's card styling. */
  scope: PushScope;
  /** The SAVED channel list carries an enabled Push channel (K4: the one switch for pairing and delivery). */
  channelEnabled: boolean;
  /** The viewer can edit the channel list above (an Operator cannot) — picks the wording of the setup hint. */
  canEditChannels?: boolean;
}

/**
 * The paired devices of a scope (plan push-relay): everyone sees their own, scope admins every
 * device. Hosts render it for everyone who may pair (Admin/Operator of the tenant, or a Global
 * Admin on the platform card), deliberately OUTSIDE any read-only fieldset: an Operator cannot
 * edit channels but pairs. The panel shows even without a Push channel, so the feature can be
 * found: it then says which channel to add; pairing and tests wait for the channel, renaming and
 * removing do not.
 */
export function PushDevicesPanel({ scope, channelEnabled, canEditChannels = true }: PushDevicesPanelProps) {
  const { getAccessToken } = useAuth();
  const { addNotification, notifyError } = useNotifications();
  const [devices, setDevices] = useState<PushDeviceDto[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [pairingOpen, setPairingOpen] = useState(false);
  const [channelRequired, setChannelRequired] = useState(false);
  const [confirmRemoveId, setConfirmRemoveId] = useState<string | null>(null);
  const [removingId, setRemovingId] = useState<string | null>(null);
  const [testingId, setTestingId] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [labelDraft, setLabelDraft] = useState("");
  const [renamingId, setRenamingId] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      try {
        const result = await listPushDevices(scope, getAccessToken);
        if (cancelled) return;
        setDevices(result.devices);
        setLoadError(null);
        setNowMs(Date.now());
      } catch (err) {
        if (cancelled) return;
        setLoadError(apiErrorText(err, "Could not load the paired devices."));
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [scope, getAccessToken, reloadKey]);

  const reload = useCallback(() => setReloadKey((k) => k + 1), []);

  const handlePaired = useCallback(
    (device: PairingDeviceDto | null) => {
      addNotification("success", "Push devices", `${device?.label || "Device"} paired.`, TOAST_KEY);
      reload();
    },
    [addNotification, reload],
  );

  const handleChannelRequired = useCallback(() => {
    setChannelRequired(true);
    setPairingOpen(false);
  }, []);

  const sendTest = async (device: PushDeviceDto) => {
    setTestingId(device.deviceId);
    try {
      const result = await testPushDevice(scope, device.deviceId, getAccessToken);
      addNotification(result.success ? "success" : "error", "Push test", result.message, TOAST_KEY);
    } catch (err) {
      notifyError("Push test", err, TOAST_KEY);
    } finally {
      setTestingId(null);
    }
  };

  const startRename = (device: PushDeviceDto) => {
    setConfirmRemoveId(null);
    setLabelDraft(device.label);
    setEditingId(device.deviceId);
  };

  const saveRename = async (device: PushDeviceDto) => {
    const label = labelDraft.trim();
    if (!label) return;
    if (label === device.label) {
      setEditingId(null);
      return;
    }
    setRenamingId(device.deviceId);
    try {
      await renamePushDevice(scope, device.deviceId, label, getAccessToken);
      setDevices((list) => (list ? list.map((d) => (d.deviceId === device.deviceId ? { ...d, label } : d)) : list));
      setEditingId(null);
      // The server cleans the name (control characters, length); the reload shows what it stored.
      reload();
    } catch (err) {
      notifyError("Push devices", err, TOAST_KEY);
    } finally {
      setRenamingId(null);
    }
  };

  const remove = async (device: PushDeviceDto) => {
    setRemovingId(device.deviceId);
    try {
      await removePushDevice(scope, device.deviceId, getAccessToken);
      setDevices((list) => (list ? list.filter((d) => d.deviceId !== device.deviceId) : list));
      setConfirmRemoveId(null);
      addNotification("info", "Push devices", `${device.label || "Device"} removed.`, TOAST_KEY);
    } catch (err) {
      notifyError("Push devices", err, TOAST_KEY);
    } finally {
      setRemovingId(null);
    }
  };

  const isPlatform = scope === "platform";
  const cardClass = isPlatform
    ? "bg-gradient-to-br from-amber-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border-2 border-amber-300 dark:border-amber-700 rounded-lg shadow-lg"
    : "bg-white rounded-lg shadow";
  const pairButtonClass = isPlatform
    ? "bg-amber-600 hover:bg-amber-700 dark:bg-amber-500 dark:hover:bg-amber-600"
    : "bg-sky-600 hover:bg-sky-700";

  const pairBlocked = channelRequired || !channelEnabled;
  const setupHint = canEditChannels ? (
    <>
      Push sends alerts to your phone or computer. To pair one, add a channel of the type{" "}
      <span className="font-medium">Push to paired devices</span> above and save.
    </>
  ) : (
    <>
      Push sends alerts to your phone or computer. Once a Tenant Admin adds a channel of the type{" "}
      <span className="font-medium">Push to paired devices</span> above, you can pair yours here.
    </>
  );
  const pairButton = (
    <button
      type="button"
      onClick={() => setPairingOpen(true)}
      disabled={pairBlocked}
      title={pairBlocked ? "Enable a Push channel above first." : undefined}
      className={`inline-flex items-center gap-2 px-4 py-2 rounded-md text-sm font-medium text-white transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${pairButtonClass}`}
    >
      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
      </svg>
      Pair a device
    </button>
  );

  return (
    <div className={cardClass}>
      <SectionCardHeader
        tone={isPlatform ? "adminAmber" : "sky"}
        iconPath={PHONE_ICON}
        title="Push devices"
        subtitle={
          isPlatform
            ? "Devices paired by Global Administrators receive the platform alerts routed to the Push channel."
            : "Devices paired by Admins and Operators of this tenant receive the Push channel's alerts. Pair your own device here."
        }
        trailing={pairButton}
      />
      <div className="p-6 space-y-3">
        {!channelEnabled ? (
          hasScopeDevice(devices, scope) ? (
            <p className="text-sm text-orange-700">
              The Push channel is off — paired devices receive nothing. Remove devices here or switch the channel back on.
            </p>
          ) : (
            <p className="text-sm text-gray-600">{setupHint}</p>
          )
        ) : (
          channelRequired && <p className="text-sm text-orange-700">Enable a Push channel above first.</p>
        )}
        {loadError && (
          <p className="text-sm text-red-700">
            {loadError}{" "}
            <button type="button" onClick={reload} className="underline hover:no-underline">
              Retry
            </button>
          </p>
        )}
        {devices === null && !loadError && <p className="text-sm text-gray-500">Loading devices…</p>}
        {devices !== null && devices.length === 0 && channelEnabled && (
          <p className="text-sm text-gray-500">No device paired yet.</p>
        )}
        {devices !== null && devices.length > 0 && (
          <ul className="divide-y divide-gray-200 dark:divide-gray-700">
            {devices.map((device) => {
              const tone = statusTone(device.status);
              const confirming = confirmRemoveId === device.deviceId;
              const editing = editingId === device.deviceId;
              const renaming = renamingId === device.deviceId;
              const busy = removingId === device.deviceId || testingId === device.deviceId || renaming;
              const canTest = device.status !== "Pending";
              // A Global Administrator's platform device shown in their home tenant (D-334): it receives
              // this tenant's alerts without a second pairing and is managed on the platform side.
              const viaPlatform = device.scope === "platform";
              return (
                <li key={device.deviceId} className="py-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                  {editing ? (
                    <input
                      type="text"
                      value={labelDraft}
                      maxLength={LABEL_MAX_CHARS}
                      onChange={(e) => setLabelDraft(e.target.value)}
                      onKeyDown={(e) => {
                        if (e.key === "Enter") {
                          e.preventDefault();
                          void saveRename(device);
                        } else if (e.key === "Escape") {
                          setEditingId(null);
                        }
                      }}
                      disabled={renaming}
                      autoFocus
                      aria-label="Device name"
                      className="basis-56 shrink-0 max-w-full min-w-0 px-2 py-1 border border-gray-300 rounded-md text-sm font-medium text-gray-900 focus:outline-none focus:ring-2 focus:ring-sky-500 focus:border-sky-500 disabled:opacity-50"
                    />
                  ) : (
                    <span className="font-medium text-gray-900 min-w-0 break-words">{device.label || "Device"}</span>
                  )}
                  <span className="text-gray-500">{platformLabel(device.platform)}</span>
                  {viaPlatform && (
                    <span
                      className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-amber-100 text-amber-800"
                      title="A Global Administrator's platform device: this is the owner's home tenant, so it receives this tenant's alerts without a second pairing."
                    >
                      Platform
                    </span>
                  )}
                  <span
                    className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${CHIP_CLASSES[tone]}`}
                    title={describePortalDeviceStatus(device.status)}
                  >
                    {device.status}
                  </span>
                  <span className="text-xs text-gray-500 min-w-0 break-words">
                    paired {new Date(device.pairedUtc).toLocaleDateString()}
                    {device.lastDeliveredUtc ? ` · delivered ${formatRelativeTime(device.lastDeliveredUtc, nowMs)}` : ""}
                    {device.lastOpenedUtc ? ` · opened ${formatRelativeTime(device.lastOpenedUtc, nowMs)}` : ""}
                    {/* The owner is named only for someone else's device — a scope admin's view. */}
                    {!device.isOwn ? ` · ${device.ownerUpn}` : ""}
                  </span>
                  {viaPlatform ? (
                    <span className="text-xs text-gray-500 sm:ml-auto">Managed under Platform › Push devices</span>
                  ) : (
                    <span className="flex flex-wrap items-center gap-2 sm:ml-auto">
                      {editing ? (
                        <>
                          <button
                            type="button"
                            onClick={() => void saveRename(device)}
                            disabled={renaming || !labelDraft.trim()}
                            className={`px-3 py-1 text-sm text-white rounded disabled:opacity-50 disabled:cursor-not-allowed transition-colors ${pairButtonClass}`}
                          >
                            {renaming ? "Saving…" : "Save"}
                          </button>
                          <button
                            type="button"
                            onClick={() => setEditingId(null)}
                            disabled={renaming}
                            className="px-2 py-1 text-sm text-gray-600 hover:text-gray-800 disabled:opacity-50"
                          >
                            Cancel
                          </button>
                        </>
                      ) : confirming ? (
                        <>
                          <span className="text-xs text-gray-600">{`Remove ${device.label || "this device"}?`}</span>
                          <button
                            type="button"
                            onClick={() => void remove(device)}
                            disabled={busy}
                            className="px-3 py-1 text-sm bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                          >
                            {removingId === device.deviceId ? "Removing…" : "Remove"}
                          </button>
                          <button
                            type="button"
                            onClick={() => setConfirmRemoveId(null)}
                            disabled={removingId === device.deviceId}
                            className="px-2 py-1 text-sm text-gray-600 hover:text-gray-800 disabled:opacity-50"
                          >
                            Cancel
                          </button>
                        </>
                      ) : (
                        <>
                          <button
                            type="button"
                            onClick={() => void sendTest(device)}
                            disabled={busy || !canTest || !channelEnabled}
                            title={
                              !channelEnabled
                                ? "The Push channel is off."
                                : canTest
                                  ? "Sends a test alert to this device."
                                  : "Only an active device can receive a test."
                            }
                            className="px-3 py-1 text-sm border border-gray-300 rounded text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                          >
                            {testingId === device.deviceId ? "Sending…" : "Send test"}
                          </button>
                          <button
                            type="button"
                            onClick={() => startRename(device)}
                            disabled={busy}
                            className="px-3 py-1 text-sm text-gray-600 hover:text-gray-900 disabled:opacity-50 transition-colors"
                          >
                            Rename
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              setEditingId(null);
                              setConfirmRemoveId(device.deviceId);
                            }}
                            disabled={busy}
                            className="px-3 py-1 text-sm text-gray-600 hover:text-red-600 disabled:opacity-50 transition-colors"
                          >
                            Remove
                          </button>
                        </>
                      )}
                    </span>
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </div>

      {pairingOpen && (
        <PairDeviceDialog
          scope={scope}
          onClose={() => setPairingOpen(false)}
          onPaired={handlePaired}
          onChannelRequired={handleChannelRequired}
        />
      )}
    </div>
  );
}
