"use client";

import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";
import { WORKER_CHANNEL } from "@/lib/push/pushClient";
import { entryFragment } from "@/lib/push/pushCore";
import { noteNotificationOpen, startReceiverTelemetry } from "@/lib/push/pushTelemetry";
import { pushHistoryUrl } from "@/lib/routes";

interface WorkerMessage {
  channel?: unknown;
  type?: unknown;
  id?: unknown;
}

/**
 * The receiver's app-wide listeners, mounted once in its layout: the usage telemetry and the worker's
 * "open" message. A notification tapped while the app is open focuses whichever receiver page is
 * showing, and the worker cannot know which; the entry opens on the history page, so any other page
 * navigates there.
 */
export function PushAppEvents() {
  const router = useRouter();
  const onHistory = ((usePathname() ?? "").replace(/\/$/, "") || "/push") === "/push";

  useEffect(() => startReceiverTelemetry(), []);

  useEffect(() => {
    const worker = "serviceWorker" in navigator ? navigator.serviceWorker : null;
    if (!worker) return;
    const onMessage = (event: MessageEvent<WorkerMessage>) => {
      if (event.data?.channel !== WORKER_CHANNEL || event.data.type !== "open" || typeof event.data.id !== "string") return;
      noteNotificationOpen();
      const fragment = entryFragment(event.data.id);
      if (onHistory) window.location.hash = fragment;
      else router.push(pushHistoryUrl(fragment));
    };
    worker.addEventListener("message", onMessage);
    return () => worker.removeEventListener("message", onMessage);
  }, [onHistory, router]);

  return null;
}
