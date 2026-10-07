"use client";

import { useSyncExternalStore } from "react";
import { detectPlatform, isAndroidDevice, isIosDevice, type Platform } from "@/lib/push/pushCore";
import {
  isPushSupported,
  isStandaloneDisplay,
  notificationPermission,
  platformInput,
  readPairCookie,
} from "@/lib/push/pushClient";

/**
 * Browser facts the receiver pages render from, read as external stores: the statically
 * exported HTML hydrates against the server snapshots and re-renders with the real values,
 * without any effect-phase setState. Values that can change while the page is open
 * (permission after a prompt, display mode) share one listener list.
 */

const listeners = new Set<() => void>();

const noopSubscribe = () => () => {};

function subscribeEnvironment(onStoreChange: () => void): () => void {
  listeners.add(onStoreChange);
  const mql = window.matchMedia("(display-mode: standalone)");
  mql.addEventListener("change", onStoreChange);
  return () => {
    listeners.delete(onStoreChange);
    mql.removeEventListener("change", onStoreChange);
  };
}

/** Call after Notification.requestPermission() or a cookie write: the snapshots re-read. */
export function notifyEnvironmentChange(): void {
  for (const listener of [...listeners]) listener();
}

function subscribeHash(onStoreChange: () => void): () => void {
  window.addEventListener("hashchange", onStoreChange);
  listeners.add(onStoreChange);
  return () => {
    window.removeEventListener("hashchange", onStoreChange);
    listeners.delete(onStoreChange);
  };
}

export function useIsClient(): boolean {
  return useSyncExternalStore(noopSubscribe, () => true, () => false);
}

export function useLocationHash(): string {
  return useSyncExternalStore(subscribeHash, () => window.location.hash, () => "");
}

export function useStandalone(): boolean {
  return useSyncExternalStore(subscribeEnvironment, isStandaloneDisplay, () => false);
}

export function useNotificationPermission(): NotificationPermission | "unsupported" {
  return useSyncExternalStore(subscribeEnvironment, notificationPermission, () => "unsupported");
}

export function usePushSupported(): boolean {
  return useSyncExternalStore(noopSubscribe, isPushSupported, () => false);
}

export function usePlatform(): Platform {
  return useSyncExternalStore(noopSubscribe, () => detectPlatform(platformInput()), () => "other");
}

export function useIsIos(): boolean {
  return useSyncExternalStore(noopSubscribe, () => isIosDevice(platformInput()), () => false);
}

export function useIsAndroid(): boolean {
  return useSyncExternalStore(noopSubscribe, () => isAndroidDevice(platformInput()), () => false);
}

export function usePairCookie(): string | null {
  return useSyncExternalStore(subscribeEnvironment, readPairCookie, () => null);
}
