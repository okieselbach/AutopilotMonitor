"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import type { DiagnosticsBuiltInSection, DiagnosticsLogPath, DiagnosticsPathsCatalog } from "@/types/diagnostics";

export interface DiagnosticsPathsCatalogState {
  /** Sections compiled into the agent — what every diagnostics package collects first. */
  builtIn: DiagnosticsBuiltInSection[];
  /** Platform-wide paths set by Global Admins, sent to every tenant's agent. */
  globalPaths: DiagnosticsLogPath[];
  loading: boolean;
}

const EMPTY: DiagnosticsPathsCatalogState = { builtIn: [], globalPaths: [], loading: true };

/**
 * What a diagnostics package collects before the tenant's own entries, from
 * GET /api/diagnostics/paths (MemberRead). Shared by the Global-Admin card and the tenant
 * Diagnostics settings. Fail-soft: errors resolve to empty lists so the editable part of
 * either card keeps working without the read-only context.
 */
export function useDiagnosticsPathsCatalog(): DiagnosticsPathsCatalogState {
  const { isAuthenticated, user, getAccessToken } = useAuth();
  const [state, setState] = useState<DiagnosticsPathsCatalogState>(EMPTY);

  // Same guard as useEditionInfo: role-less members would just 401/403 on a MemberRead route.
  const hasTenantRole = !!user && (user.isTenantAdmin || user.isGlobalAdmin || user.role != null);

  useEffect(() => {
    if (!isAuthenticated || !hasTenantRole) return;
    let cancelled = false;
    const run = async () => {
      try {
        const res = await authenticatedFetch(api.diagnostics.paths(), getAccessToken);
        if (!res.ok) {
          if (!cancelled) setState((s) => ({ ...s, loading: false }));
          return;
        }
        const data = (await res.json()) as Partial<DiagnosticsPathsCatalog>;
        if (!cancelled) {
          setState({
            builtIn: Array.isArray(data.builtIn) ? data.builtIn : [],
            globalPaths: Array.isArray(data.globalPaths) ? data.globalPaths : [],
            loading: false,
          });
        }
      } catch {
        if (!cancelled) setState((s) => ({ ...s, loading: false }));
      }
    };
    void run();
    return () => { cancelled = true; };
  }, [isAuthenticated, hasTenantRole, getAccessToken]);

  return state;
}
