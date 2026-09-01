"use client";

import { useState, useEffect } from "react";
import {
  DEMO_MODE_STORAGE_KEY,
  effectiveGlobalAdminMode,
  readDemoParam,
  stripDemoParam,
} from "@/lib/demoMode";

interface UseAdminModeReturn {
  adminMode: boolean;
  setAdminMode: (value: boolean) => void;
  globalAdminMode: boolean;
  setGlobalAdminMode: (value: boolean) => void;
  /**
   * Presentation mode for live demos — hides the Global-Admin toggle, the Global-Admin badge and
   * every operator-only surface. Read-only by design: it is armed via `?demo=1` / cleared via
   * `?demo=0` (both consumed from the address bar), never by a control inside the portal.
   */
  demoMode: boolean;
}

/**
 * Reads the demo-mode intent from the current URL and falls back to the stored flag. Used by the
 * lazy initializer so the very first render already knows — otherwise operator-only UI would flash
 * on screen for one frame before the effect below settles it.
 */
function resolveInitialDemoMode(): boolean {
  const fromUrl = readDemoParam(window.location.search);
  if (fromUrl !== null) return fromUrl;
  return localStorage.getItem(DEMO_MODE_STORAGE_KEY) === "true";
}

/**
 * Guards the URL consumption: nine components mount this hook on a single page, and the parameter
 * must be persisted and stripped exactly once per page load.
 */
let demoParamConsumed = false;

/**
 * Manages adminMode, globalAdminMode and demoMode state with localStorage persistence.
 * Changes are persisted to localStorage and broadcast via a 'localStorageChange'
 * custom event so all components on the same page stay in sync.
 * Cross-tab synchronization uses the native 'storage' event.
 *
 * Demo mode forces globalAdminMode off for every consumer (nav, scope hooks, platform
 * notifications) while leaving the STORED value untouched, so clearing demo mode restores the
 * operator's usual view. See lib/demoMode.ts — presentation only, never a security boundary.
 */
export function useAdminMode(): UseAdminModeReturn {
  const [adminMode, setAdminModeState] = useState<boolean>(() => {
    if (typeof window !== "undefined") {
      return localStorage.getItem("adminMode") === "true";
    }
    return false;
  });

  const [globalAdminMode, setGlobalAdminModeState] = useState<boolean>(() => {
    if (typeof window !== "undefined") {
      return localStorage.getItem("globalAdminMode") === "true";
    }
    return false;
  });

  const [demoMode, setDemoModeState] = useState<boolean>(() => {
    if (typeof window !== "undefined") {
      return resolveInitialDemoMode();
    }
    return false;
  });

  // Consume the ?demo= parameter: persist it, then strip it from the address bar so nothing shows
  // in a screenshot and a reload keeps the mode. Runs once per page load across all hook instances.
  useEffect(() => {
    if (demoParamConsumed) return;
    demoParamConsumed = true;

    const fromUrl = readDemoParam(window.location.search);
    if (fromUrl === null) return;

    // No setState here: every instance's lazy initializer already read the same parameter, and any
    // component mounting after the strip reads the value we just persisted. The broadcast covers
    // instances that initialized from a stale stored value.
    localStorage.setItem(DEMO_MODE_STORAGE_KEY, fromUrl.toString());
    window.dispatchEvent(new Event("localStorageChange"));

    const cleaned = stripDemoParam(
      window.location.pathname + window.location.search + window.location.hash
    );
    window.history.replaceState(null, "", cleaned);
  }, []);

  // Persist to localStorage and notify same-tab listeners on change
  useEffect(() => {
    localStorage.setItem("adminMode", adminMode.toString());
    window.dispatchEvent(new Event("localStorageChange"));
  }, [adminMode]);

  useEffect(() => {
    localStorage.setItem("globalAdminMode", globalAdminMode.toString());
    window.dispatchEvent(new Event("localStorageChange"));
  }, [globalAdminMode]);

  // Sync from external changes (cross-tab via 'storage', same-tab via 'localStorageChange')
  useEffect(() => {
    const handleStorageChange = (e: StorageEvent) => {
      if (e.key === "adminMode" && e.newValue !== null) {
        setAdminModeState(e.newValue === "true");
      }
      if (e.key === "globalAdminMode" && e.newValue !== null) {
        setGlobalAdminModeState(e.newValue === "true");
      }
      if (e.key === DEMO_MODE_STORAGE_KEY && e.newValue !== null) {
        setDemoModeState(e.newValue === "true");
      }
    };

    const handleCustomStorageChange = () => {
      const newAdminMode = localStorage.getItem("adminMode") === "true";
      const newGlobalMode = localStorage.getItem("globalAdminMode") === "true";
      const newDemoMode = localStorage.getItem(DEMO_MODE_STORAGE_KEY) === "true";
      setAdminModeState((prev) => (prev !== newAdminMode ? newAdminMode : prev));
      setGlobalAdminModeState((prev) =>
        prev !== newGlobalMode ? newGlobalMode : prev
      );
      setDemoModeState((prev) => (prev !== newDemoMode ? newDemoMode : prev));
    };

    window.addEventListener("storage", handleStorageChange);
    window.addEventListener("localStorageChange", handleCustomStorageChange);
    return () => {
      window.removeEventListener("storage", handleStorageChange);
      window.removeEventListener(
        "localStorageChange",
        handleCustomStorageChange
      );
    };
  }, []);

  return {
    adminMode,
    setAdminMode: setAdminModeState,
    globalAdminMode: effectiveGlobalAdminMode(globalAdminMode, demoMode),
    // No-op while presenting. The toggle is hidden in demo mode anyway; this keeps any other
    // caller from re-arming the global view behind the operator's back.
    setGlobalAdminMode: demoMode ? () => {} : setGlobalAdminModeState,
    demoMode,
  };
}
