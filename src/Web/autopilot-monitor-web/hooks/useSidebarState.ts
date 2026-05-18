"use client";

import { useState, useCallback, useEffect } from "react";
import { trackEvent, setSidebarStateContext } from "@/lib/appInsights";

export type CollapseState = "full" | "icons" | "hidden";

const STORAGE_KEY = "sidebar-collapse-state";
const CYCLE_ORDER: CollapseState[] = ["full", "icons", "hidden"];

export function readSidebarState(): CollapseState {
  if (typeof window === "undefined") return "full";
  const stored = localStorage.getItem(STORAGE_KEY);
  if (stored === "full" || stored === "icons" || stored === "hidden") return stored;
  return "full";
}

export function useSidebarState() {
  const [collapseState, setCollapseStateRaw] = useState<CollapseState>(readSidebarState);

  useEffect(() => {
    setSidebarStateContext(collapseState);
  }, [collapseState]);

  const setCollapseState = useCallback((state: CollapseState) => {
    const previous = readSidebarState();
    setCollapseStateRaw(state);
    if (typeof window !== "undefined") {
      localStorage.setItem(STORAGE_KEY, state);
    }
    if (previous !== state) {
      trackEvent("sidebar_state_changed", { from: previous, to: state });
    }
  }, []);

  const cycleCollapseState = useCallback(() => {
    setCollapseState(
      CYCLE_ORDER[(CYCLE_ORDER.indexOf(readSidebarState()) + 1) % CYCLE_ORDER.length]
    );
  }, [setCollapseState]);

  return { collapseState, setCollapseState, cycleCollapseState };
}
