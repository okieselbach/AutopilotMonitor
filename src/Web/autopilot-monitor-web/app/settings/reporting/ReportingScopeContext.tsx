"use client";

import { createContext, useContext } from "react";
import type { GlobalAdminScope } from "@/hooks";

// One scope instance for the reporting subtree: the header (ReportingSidebar) owns the banner and the
// tenant selector, the sections read the selection. A second useGlobalAdminScope() call would hold its
// own selection state and never see the header's changes.
const ReportingScopeContext = createContext<GlobalAdminScope | null>(null);

export const ReportingScopeProvider = ReportingScopeContext.Provider;

export function useReportingScope(): GlobalAdminScope {
  const scope = useContext(ReportingScopeContext);
  if (!scope) throw new Error("useReportingScope must be used inside the reporting layout");
  return scope;
}
