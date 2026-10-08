"use client";

import { useCallback, useEffect, useState } from "react";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { PairDeviceDialog } from "@/components/push/PairDeviceDialog";
import { useAuth } from "@/contexts/AuthContext";
import { useNotifications } from "@/contexts/NotificationContext";
import { apiErrorText } from "@/lib/apiClient";
import { formatRelativeTime } from "@/lib/push/pushFormat";
import { describePortalDeviceStatus, platformLabel, statusTone, type PushScope, type StatusTone } from "@/lib/pushPortal";
import { listPushDevices, removePushDevice, testPushDevice } from "@/lib/pushPortalApi";
import type { PairingDeviceDto, PushDeviceDto } from "@/utils/wire-types.generated";

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
}

/**
 * The paired devices of a scope (plan push-relay): everyone sees their own, scope admins every
 * device. Hosts render it only when the SAVED channel list carries an enabled Push channel (K4)
 * and the caller may pair (Admin/Operator of the tenant, or a Global Admin on the platform card),
 * and deliberately OUTSIDE any read-only fieldset: an Operator cannot edit channels but pairs.
 */
export function PushDevicesPanel({ scope }: PushDevicesPanelProps) {
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

  const pairButton = (
    <button
      type="button"
      onClick={() => setPairingOpen(true)}
      disabled={channelRequired}
      title={channelRequired ? "Enable a Push channel above first." : undefined}
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
        {channelRequired && (
          <p className="text-sm text-orange-700">Enable a Push channel above first.</p>
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
        {devices !== null && devices.length === 0 && (
          <p className="text-sm text-gray-500">No device paired yet.</p>
        )}
        {devices !== null && devices.length > 0 && (
          <ul className="divide-y divide-gray-200 dark:divide-gray-700">
            {devices.map((device) => {
              const tone = statusTone(device.status);
              const confirming = confirmRemoveId === device.deviceId;
              const busy = removingId === device.deviceId || testingId === device.deviceId;
              const canTest = device.status !== "Pending";
              // A Global Administrator's platform device shown in their home tenant (D-334): it receives
              // this tenant's alerts without a second pairing and is managed on the platform side.
              const viaPlatform = device.scope === "platform";
              return (
                <li key={device.deviceId} className="py-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                  <span className="font-medium text-gray-900 min-w-0 break-words">{device.label || "Device"}</span>
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
                      {confirming ? (
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
                            disabled={busy || !canTest}
                            title={canTest ? "Sends a test alert to this device." : "Only an active device can receive a test."}
                            className="px-3 py-1 text-sm border border-gray-300 rounded text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                          >
                            {testingId === device.deviceId ? "Sending…" : "Send test"}
                          </button>
                          <button
                            type="button"
                            onClick={() => setConfirmRemoveId(device.deviceId)}
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
