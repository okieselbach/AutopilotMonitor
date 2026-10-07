"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import {
  APP_VERSION,
  cleanLabel,
  defaultLabel,
  LABEL_MAX_CHARS,
  normalizePairingCode,
  PAIRING_CODE_LENGTH,
  parseFragment,
  writeMeta,
} from "@/lib/push/pushCore";
import {
  describePushError,
  getDevice,
  pairBegin,
  pairRedeem,
  PUSH_ERROR_CODES,
  PushApiError,
  type PairBeginResponse,
} from "@/lib/push/pushApi";
import {
  clearPairCookie,
  pairedMeta,
  registerPushWorker,
  requestPersistentStorage,
  subscribeToPush,
  subscriptionKeys,
  wipeLocalDevice,
  writePairCookie,
} from "@/lib/push/pushClient";
import {
  notifyEnvironmentChange,
  useIsAndroid,
  useIsClient,
  useIsIos,
  useNotificationPermission,
  usePairCookie,
  usePlatform,
  usePushSupported,
  useStandalone,
} from "../pushEnvironment";

type Phase =
  | { kind: "idle" }
  | { kind: "working"; step: string }
  | { kind: "waiting"; token: string; pollSeconds: number }
  | { kind: "done" }
  | { kind: "error"; message: string };

/**
 * The prefetched `POST push/pair/begin` for one normalised code (K10). `pending` keeps the
 * in-flight promise so a click during the request awaits that same request instead of a second
 * one; `invalid` is the server's 404 (unknown, expired or used code) and is shown inline;
 * `failed` is anything else (network, rate limit) and the click simply tries again.
 */
type BeginState =
  | { kind: "pending"; code: string; promise: Promise<PairBeginResponse> }
  | { kind: "ready"; code: string; begin: PairBeginResponse }
  | { kind: "invalid"; code: string; message: string }
  | { kind: "failed"; code: string };

/** Typing pauses this long before the code is sent to the server for checking. */
const BEGIN_PREFETCH_DEBOUNCE_MS = 300;

const PRIMARY_BUTTON =
  "w-full px-4 py-3 bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed font-medium";

/** The pairing code of the URL fragment, read once at first render (null on the server). */
function readPairFragment(): string | null {
  if (typeof window === "undefined") return null;
  const target = parseFragment(window.location.hash);
  return target?.kind === "pair" ? target.code : null;
}

function isCodeInvalid(error: unknown): boolean {
  return error instanceof PushApiError && (error.status === 404 || error.is(PUSH_ERROR_CODES.pairingCodeInvalid));
}

/**
 * Pairing (K7, K10, K12): the code comes from the QR link (#p=), from the Safari hand-off cookie,
 * or is typed. On iOS the page only acts as a home-screen app; a Safari tab would burn the code
 * on a subscription nobody can use, so it shows the install steps and does nothing else.
 *
 * The permission prompt and `pushManager.subscribe()` need the click's user activation on
 * WebKit and Firefox, and that window is seconds. So everything that takes a network round trip
 * happens before the click: the service worker is registered on mount, the code is checked
 * (`pair/begin`, which also hands over the VAPID key) as soon as it is complete, and the click
 * runs prompt → subscribe back to back with no await in between.
 */
export default function PushPairPage() {
  const isClient = useIsClient();
  const standalone = useStandalone();
  const platform = usePlatform();
  const isIos = useIsIos();
  const isAndroid = useIsAndroid();
  const supported = usePushSupported();
  const permission = useNotificationPermission();
  const cookieCode = usePairCookie();

  // The fragment is consumed once: the code is a one-shot secret and must not stay in the URL
  // bar, the history or a tab sync (F10). The history page's #e/<id> deep link is untouched.
  const [fragmentCode] = useState(readPairFragment);
  useEffect(() => {
    if (fragmentCode && parseFragment(window.location.hash)?.kind === "pair") {
      window.history.replaceState(null, "", window.location.pathname + window.location.search);
    }
  }, [fragmentCode]);

  const [codeInput, setCodeInput] = useState<string | null>(null);
  const [labelInput, setLabelInput] = useState<string | null>(null);
  const [phase, setPhase] = useState<Phase>({ kind: "idle" });
  const [storagePersisted, setStoragePersisted] = useState<boolean | null>(null);
  const [waitedSeconds, setWaitedSeconds] = useState(0);
  const [registration, setRegistration] = useState<ServiceWorkerRegistration | null>(null);
  const [beginState, setBeginState] = useState<BeginState | null>(null);

  const effectiveCode = codeInput ?? fragmentCode ?? cookieCode ?? "";
  const normalizedCode = normalizePairingCode(effectiveCode);
  const effectiveLabel = labelInput ?? defaultLabel(platform);
  const iosInstallNeeded = isClient && isIos && !standalone;
  const canPrepare = supported && !iosInstallNeeded;

  // Safari tab on iOS with a QR link: hand the code to the home-screen app through the cookie.
  useEffect(() => {
    if (iosInstallNeeded && fragmentCode) {
      writePairCookie(fragmentCode);
      notifyEnvironmentChange();
    }
  }, [iosInstallNeeded, fragmentCode]);

  // The service worker is registered and ready before the click, so subscribe() follows the
  // permission prompt without waiting for sw.js. A failure here is retried by the click.
  useEffect(() => {
    if (!canPrepare) return;
    let cancelled = false;
    const run = async () => {
      try {
        const ready = await registerPushWorker();
        if (!cancelled) setRegistration(ready);
      } catch {
        // The click handler registers again and reports the error there.
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [canPrepare]);

  // Check the code (and fetch the VAPID key) as soon as it is complete, debounced while typing.
  useEffect(() => {
    if (!canPrepare || !normalizedCode) return;
    const code = normalizedCode;
    let cancelled = false;
    const timer = setTimeout(() => {
      const promise = pairBegin(code);
      setBeginState({ kind: "pending", code, promise });
      promise.then(
        (begin) => {
          if (!cancelled) setBeginState({ kind: "ready", code, begin });
        },
        (error: unknown) => {
          if (cancelled) return;
          setBeginState(isCodeInvalid(error) ? { kind: "invalid", code, message: describePushError(error) } : { kind: "failed", code });
        },
      );
    }, BEGIN_PREFETCH_DEBOUNCE_MS);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [canPrepare, normalizedCode]);

  // Poll until the owner confirms on the computer (the device stays Pending until then, K7).
  const waitingToken = phase.kind === "waiting" ? phase.token : null;
  const pollSeconds = phase.kind === "waiting" ? phase.pollSeconds : 0;
  useEffect(() => {
    if (!waitingToken) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      try {
        const device = await getDevice(waitingToken);
        if (cancelled) return;
        if (device.status === "Active") {
          setPhase({ kind: "done" });
          return;
        }
      } catch (error) {
        if (cancelled) return;
        if (error instanceof PushApiError && (error.status === 404 || error.status === 401)) {
          // A rejection deletes the Pending row on the server, so it answers 401; expiry
          // removes it too. Either way the local device is gone.
          await wipeLocalDevice().catch(() => {});
          if (cancelled) return;
          setPhase({ kind: "error", message: "The pairing was rejected or expired on your computer. Start again with a new code." });
          return;
        }
        // Network hiccup: keep polling.
      }
      if (cancelled) return;
      setWaitedSeconds((s) => s + pollSeconds);
      timer = setTimeout(() => void tick(), pollSeconds * 1000);
    };
    timer = setTimeout(() => void tick(), pollSeconds * 1000);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [waitingToken, pollSeconds]);

  const start = async () => {
    const code = normalizedCode;
    if (!code) {
      setPhase({ kind: "error", message: `Enter the ${PAIRING_CODE_LENGTH}-character code shown in the portal.` });
      return;
    }
    const label = cleanLabel(effectiveLabel) || defaultLabel(platform);
    try {
      // Normal path: the begin response and the worker are already here from the effects above.
      // Fallback (the click came within the debounce, during the request, or after a transient
      // failure): await them FIRST and prompt afterwards — the activation window may be lost on
      // WebKit, but prompting for a code the server has not accepted is worse (K10).
      let begin: PairBeginResponse;
      if (beginState?.code === code && beginState.kind === "ready") {
        begin = beginState.begin;
      } else {
        setPhase({ kind: "working", step: "Checking the code…" });
        begin = beginState?.code === code && beginState.kind === "pending" ? await beginState.promise : await pairBegin(code);
      }
      let ready = registration;
      if (!ready) {
        setPhase({ kind: "working", step: "Preparing…" });
        ready = await registerPushWorker();
      }

      // From here to subscribe(): no network, no IndexedDB — only the prompt and the subscription.
      setPhase({ kind: "working", step: "Asking for permission…" });
      const granted = await Notification.requestPermission();
      notifyEnvironmentChange();
      if (granted !== "granted") {
        setPhase({ kind: "error", message: "Notifications were not allowed. Allow them for this app in the browser settings and try again." });
        return;
      }
      let subscription: PushSubscription;
      try {
        subscription = await subscribeToPush(ready, begin.vapidPublicKey);
      } catch {
        setPhase({ kind: "error", message: "The browser refused to create a push subscription. Check that notifications are allowed for this app and try again." });
        return;
      }
      const keys = subscriptionKeys(subscription);

      setPhase({ kind: "working", step: "Pairing…" });
      const result = await pairRedeem({ code, kid: begin.kid, ...keys, label, platform, appVersion: APP_VERSION });
      await writeMeta(
        pairedMeta({
          deviceToken: result.deviceToken,
          deviceId: result.deviceId,
          endpoint: keys.endpoint,
          kid: begin.kid,
          vapidPublicKey: begin.vapidPublicKey,
          platform,
          label,
        }),
      );
      clearPairCookie();
      notifyEnvironmentChange();
      setStoragePersisted(await requestPersistentStorage());
      setWaitedSeconds(0);
      const poll = Math.min(Math.max(result.pollSeconds || 3, 2), 30);
      setPhase(result.status === "Active" ? { kind: "done" } : { kind: "waiting", token: result.deviceToken, pollSeconds: poll });
    } catch (error) {
      setPhase({ kind: "error", message: describePushError(error) });
    }
  };

  if (!isClient) {
    return <p className="text-sm text-gray-500">Checking this device…</p>;
  }

  if (iosInstallNeeded) {
    return (
      <Card title="Install the app first">
        <p className="text-sm text-gray-600">
          On iPhone and iPad, alerts only reach an app on the Home Screen. Add this page there, then open it from the Home Screen to finish pairing.
        </p>
        <ol className="list-decimal pl-5 text-sm text-gray-700 space-y-1">
          <li>Tap the Share button in Safari.</li>
          <li>Choose <span className="font-medium">Add to Home Screen</span>.</li>
          <li>Open <span className="font-medium">AM Alerts</span> from the Home Screen.</li>
        </ol>
        {fragmentCode ? (
          <p className="text-xs text-gray-500">The code from this link is kept for ten minutes so you can continue in the app.</p>
        ) : (
          <p className="text-xs text-gray-500">In the app, type the code shown in the portal.</p>
        )}
      </Card>
    );
  }

  if (!supported) {
    return (
      <Card title="Push is not available here">
        <p className="text-sm text-gray-600">
          This browser does not support web push. Use Chrome or Edge on Windows or Android, Safari on a Mac, or add the page to the Home Screen on an iPhone.
        </p>
      </Card>
    );
  }

  if (phase.kind === "done") {
    return (
      <Card title="Paired">
        <p className="text-sm text-gray-600">Alerts for your tenant now reach this device.</p>
        {storagePersisted === false && (
          <p className="text-xs text-gray-500">The browser did not grant persistent storage; the history may be cleared when space is low.</p>
        )}
        <Link href="/push" className="inline-block text-sm font-medium text-green-700 hover:underline">
          Open the history
        </Link>
      </Card>
    );
  }

  if (phase.kind === "waiting") {
    return (
      <Card title="Waiting for confirmation">
        <p className="text-sm text-gray-600">Confirm the new device on your computer. This page updates by itself.</p>
        <p className="text-xs text-gray-500">
          {waitedSeconds >= 120
            ? "Still waiting. The code is valid for ten minutes; after that, start again with a new one."
            : "Checking every few seconds…"}
        </p>
      </Card>
    );
  }

  const working = phase.kind === "working";
  const blocked = permission === "denied";
  const codeInvalid = beginState?.kind === "invalid" && beginState.code === normalizedCode ? beginState.message : null;

  return (
    <Card title="Pair this device">
      <p className="text-sm text-gray-600">
        Enter the code shown in the portal under Notifications, then allow notifications when the browser asks.
      </p>
      <label className="block">
        <span className="text-xs font-medium text-gray-700">Pairing code</span>
        <input
          type="text"
          inputMode="text"
          autoCapitalize="characters"
          autoComplete="off"
          spellCheck={false}
          value={effectiveCode}
          onChange={(e) => setCodeInput(e.target.value.toUpperCase())}
          disabled={working}
          placeholder="e.g. 7Q3M-K9T2-XH4"
          className="mt-1 w-full px-3 py-2.5 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 font-mono tracking-widest text-lg focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
        />
      </label>
      <label className="block">
        <span className="text-xs font-medium text-gray-700">Device name</span>
        <input
          type="text"
          value={effectiveLabel}
          maxLength={LABEL_MAX_CHARS}
          onChange={(e) => setLabelInput(e.target.value)}
          disabled={working}
          className="mt-1 w-full px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
        />
        <span className="block text-xs text-gray-500 mt-1">Shown in the portal&apos;s device list.</span>
      </label>

      {blocked && (
        <p className="text-sm text-red-700 dark:text-red-400">
          Notifications are blocked for this site. Allow them in the browser or system settings, then try again.
        </p>
      )}
      {codeInvalid && <p className="text-sm text-red-700 dark:text-red-400">{codeInvalid}</p>}
      {phase.kind === "error" && phase.message !== codeInvalid && (
        <p className="text-sm text-red-700 dark:text-red-400">{phase.message}</p>
      )}

      <button type="button" onClick={() => void start()} disabled={working || blocked || codeInvalid !== null} className={PRIMARY_BUTTON}>
        {working ? phase.step : "Enable notifications"}
      </button>

      {isAndroid && !standalone && (
        <p className="text-xs text-gray-500">Adding this page to the Home screen is recommended: Android keeps notification permission for installed apps.</p>
      )}
    </Card>
  );
}

function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="bg-white rounded-lg shadow p-5 space-y-4">
      <h1 className="text-lg font-semibold text-gray-900">{title}</h1>
      {children}
    </div>
  );
}
