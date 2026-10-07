"use client";

import { useEffect, useState } from "react";
import { ModalPortal } from "@/components/ModalPortal";
import { useAuth } from "@/contexts/AuthContext";
import { useCopy } from "@/hooks/useCopy";
import { useLatest } from "@/hooks/useLatest";
import { apiErrorText } from "@/lib/apiClient";
import {
  formatCountdown,
  formatPairingCode,
  PAIRING_GRANT_SECONDS,
  pairingSecondsLeft,
  pairPageUrl,
  platformLabel,
  type PairingStatus,
  type PushScope,
} from "@/lib/pushPortal";
import { confirmPairing, createPairing, getPairingStatus, isPushChannelRequired, rejectPairing } from "@/lib/pushPortalApi";
import type { CreatePairingResponse, PairingDeviceDto } from "@/utils/wire-types.generated";

const POLL_MS = 3000;

interface PairDeviceDialogProps {
  scope: PushScope;
  onClose: () => void;
  /** The device is Active now; the host refreshes its list and toasts. */
  onPaired: (device: PairingDeviceDto | null) => void;
  /** POST pairings answered 409 PushChannelRequired — the host shows the hint and disables pairing. */
  onChannelRequired: () => void;
}

/**
 * Pairing from the PC side (plan push-relay K7/K8): one code per dialog, shown as QR image, as
 * grouped text and as link; the phone redeems it, the dialog polls until a device waits for
 * confirmation and the owner confirms or rejects it here. The code is valid for ten minutes
 * and never leaves the fragment of the link, so nothing here reaches a server log.
 *
 * Expiry is the server's call: the poll runs until it reports Confirmed, Rejected or Expired.
 * The countdown is anchored on the moment the code arrived on this clock (not on `expiresUtc`,
 * which a PC clock ten minutes ahead would read as already past) and at zero only changes the
 * label.
 */
export function PairDeviceDialog({ scope, onClose, onPaired, onChannelRequired }: PairDeviceDialogProps) {
  const { getAccessToken } = useAuth();
  const { copied, copy } = useCopy();
  const [attempt, setAttempt] = useState(0);
  const [pairing, setPairing] = useState<CreatePairingResponse | null>(null);
  const [qrDataUrl, setQrDataUrl] = useState<string | null>(null);
  const [status, setStatus] = useState<PairingStatus>("Pending");
  const [device, setDevice] = useState<PairingDeviceDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [receivedAtMs, setReceivedAtMs] = useState<number | null>(null);

  // The host's callbacks are read through refs so an inline arrow in the host cannot re-run the
  // effects below (a re-run of the create effect would mint a second code).
  const onPairedRef = useLatest(onPaired);
  const onCloseRef = useLatest(onClose);
  const onChannelRequiredRef = useLatest(onChannelRequired);

  // One pairing code per attempt; the QR image is rendered client-side from the link.
  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      try {
        const created = await createPairing(scope, getAccessToken);
        if (cancelled) return;
        const arrivedAt = Date.now();
        setPairing(created);
        setReceivedAtMs(arrivedAt);
        setNowMs(arrivedAt);
        try {
          const { toDataURL } = await import("qrcode");
          const dataUrl = await toDataURL(created.url, { margin: 1, width: 224 });
          if (!cancelled) setQrDataUrl(dataUrl);
        } catch {
          // The typed code and the link still work without the image.
        }
      } catch (err) {
        if (cancelled) return;
        if (isPushChannelRequired(err)) onChannelRequiredRef.current();
        setError(apiErrorText(err, "Could not start pairing."));
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [attempt, scope, getAccessToken, onChannelRequiredRef]);

  const pairingId = pairing?.pairingId ?? null;
  const secondsLeft = receivedAtMs === null ? PAIRING_GRANT_SECONDS : pairingSecondsLeft(receivedAtMs, nowMs);
  const polling = pairingId !== null && (status === "Pending" || status === "Redeemed");
  const countingDown = polling && secondsLeft > 0;
  // The grant expires lazily on the server (GET pairings/{id} reports Expired past expiresUtc);
  // the local zero only changes the wording until that answer arrives.
  const expiryText = secondsLeft > 0 ? `Expires in ${formatCountdown(secondsLeft)}` : "The code may have expired";

  // Poll the pairing while the phone can still act on it (Pending → Redeemed → Confirmed).
  useEffect(() => {
    if (!pairingId || !polling) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      try {
        const result = await getPairingStatus(scope, pairingId, getAccessToken);
        if (cancelled) return;
        const next = result.status as PairingStatus;
        setStatus(next);
        if (result.device) setDevice(result.device);
        if (next === "Confirmed") {
          onPairedRef.current(result.device ?? null);
          onCloseRef.current();
          return;
        }
        if (next === "Rejected" || next === "Expired") return;
      } catch {
        if (cancelled) return;
        // Transient failure: the next tick tries again.
      }
      if (!cancelled) timer = setTimeout(() => void tick(), POLL_MS);
    };
    timer = setTimeout(() => void tick(), POLL_MS);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [pairingId, polling, scope, getAccessToken, onPairedRef, onCloseRef]);

  // Countdown tick while the code is live and the label still moves.
  useEffect(() => {
    if (!countingDown) return;
    const interval = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(interval);
  }, [countingDown]);

  const confirm = async () => {
    if (!pairingId) return;
    setBusy(true);
    setError(null);
    try {
      await confirmPairing(scope, pairingId, getAccessToken);
      onPaired(device);
      onClose();
    } catch (err) {
      setError(apiErrorText(err, "Could not confirm the device."));
      setBusy(false);
    }
  };

  const reject = async () => {
    if (!pairingId) return;
    setBusy(true);
    setError(null);
    try {
      await rejectPairing(scope, pairingId, getAccessToken);
      setStatus("Rejected");
    } catch (err) {
      setError(apiErrorText(err, "Could not reject the device."));
    } finally {
      setBusy(false);
    }
  };

  const startAgain = () => {
    setPairing(null);
    setQrDataUrl(null);
    setReceivedAtMs(null);
    setStatus("Pending");
    setDevice(null);
    setError(null);
    setAttempt((a) => a + 1);
  };

  const close = () => {
    if (!busy) onClose();
  };

  const code = pairing ? formatPairingCode(pairing.code) : "";

  return (
    <ModalPortal>
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={close}>
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="pair-device-title"
          className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4 max-h-[90vh] overflow-y-auto"
          onClick={(e) => e.stopPropagation()}
        >
          <div className="p-6 space-y-4">
            <div className="flex items-start justify-between gap-3">
              <h3 id="pair-device-title" className="text-lg font-semibold text-gray-900">Pair a device</h3>
              <button
                type="button"
                onClick={close}
                disabled={busy}
                className="p-1 text-gray-400 hover:text-gray-600 disabled:opacity-50 transition-colors"
                aria-label="Close"
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            {!pairing && !error && (
              <div className="flex items-center gap-3 text-sm text-gray-600 py-6 justify-center">
                <svg className="animate-spin h-5 w-5 text-gray-400" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"></path>
                </svg>
                Creating a pairing code…
              </div>
            )}

            {pairing && status === "Pending" && (
              <>
                <div className="flex flex-col items-center gap-3">
                  {qrDataUrl ? (
                    // eslint-disable-next-line @next/next/no-img-element -- generated data: URI, not an asset
                    <img src={qrDataUrl} alt="QR code of the pairing link" width={224} height={224} className="rounded border border-gray-200" />
                  ) : (
                    <div className="w-56 h-56 rounded border border-dashed border-gray-300 flex items-center justify-center text-xs text-gray-400">
                      QR code
                    </div>
                  )}
                  <div className="flex flex-wrap items-center justify-center gap-2">
                    <span className="font-mono text-2xl tracking-widest text-gray-900 select-all">{code}</span>
                    <button
                      type="button"
                      onClick={() => void copy(pairing.code, "code")}
                      className="px-2.5 py-1 text-xs font-medium border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 transition-colors"
                    >
                      {copied === "code" ? "Copied" : "Copy code"}
                    </button>
                  </div>
                  <div className="flex items-center gap-2 max-w-full">
                    <span className="font-mono text-xs text-gray-500 truncate select-all">{pairing.url}</span>
                    <button
                      type="button"
                      onClick={() => void copy(pairing.url, "url")}
                      className="flex-shrink-0 text-xs font-medium text-sky-600 hover:text-sky-700 transition-colors"
                    >
                      {copied === "url" ? "Copied" : "Copy link"}
                    </button>
                  </div>
                  <p className="text-xs text-gray-500">{expiryText}</p>
                </div>
                <p className="text-sm text-gray-600">
                  On the phone: scan the code or open <span className="font-mono text-xs">{pairPageUrl(pairing.url)}</span> and type the code.
                  On iPhone add the page to the Home Screen first.
                </p>
              </>
            )}

            {pairing && status === "Redeemed" && (
              <div className="space-y-3">
                <div className="rounded-lg border border-orange-200 bg-orange-50 p-4">
                  <p className="text-sm text-gray-900">
                    <span className="font-semibold">{device?.label || "A device"}</span>
                    {device ? ` (${platformLabel(device.platform)})` : ""} wants to pair.
                  </p>
                  <p className="text-xs text-gray-600 mt-1">Confirm only if this is your device. {expiryText}.</p>
                </div>
                <div className="flex justify-end gap-3">
                  <button
                    type="button"
                    onClick={() => void reject()}
                    disabled={busy}
                    className="px-4 py-2 border border-red-300 text-red-700 bg-white rounded-md hover:bg-red-50 transition-colors disabled:opacity-50 text-sm"
                  >
                    Reject
                  </button>
                  <button
                    type="button"
                    onClick={() => void confirm()}
                    disabled={busy}
                    className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 transition-colors disabled:opacity-50 text-sm"
                  >
                    {busy ? "Confirming…" : "Confirm"}
                  </button>
                </div>
              </div>
            )}

            {pairing && (status === "Expired" || status === "Rejected") && (
              <div className="space-y-3">
                <p className="text-sm text-gray-700">
                  {status === "Expired" ? "The code expired." : "The pairing was rejected."}
                </p>
                <div className="flex justify-end gap-3">
                  <button
                    type="button"
                    onClick={close}
                    className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors text-sm"
                  >
                    Close
                  </button>
                  <button
                    type="button"
                    onClick={startAgain}
                    className="px-4 py-2 bg-sky-600 text-white rounded-md hover:bg-sky-700 transition-colors text-sm"
                  >
                    Start again
                  </button>
                </div>
              </div>
            )}

            {error && (
              <div className="space-y-3">
                <p className="text-sm text-red-700">{error}</p>
                {!pairing && (
                  <div className="flex justify-end gap-3">
                    <button
                      type="button"
                      onClick={close}
                      className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors text-sm"
                    >
                      Close
                    </button>
                    <button
                      type="button"
                      onClick={startAgain}
                      className="px-4 py-2 bg-sky-600 text-white rounded-md hover:bg-sky-700 transition-colors text-sm"
                    >
                      Try again
                    </button>
                  </div>
                )}
              </div>
            )}
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
