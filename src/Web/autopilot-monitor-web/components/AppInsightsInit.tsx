"use client";

import { useEffect } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import { useTheme } from "@/contexts/ThemeContext";
import { initAppInsights, setTelemetryContext } from "@/lib/appInsights";
import { readSidebarState } from "@/hooks/useSidebarState";

export default function AppInsightsInit() {
  const connectionString = process.env.NEXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING ?? "";
  const { user } = useAuth();
  const { tenantId } = useTenant();
  const { theme } = useTheme();

  useEffect(() => {
    if (connectionString) {
      initAppInsights(connectionString);
    }
  }, [connectionString]);

  useEffect(() => {
    setTelemetryContext(
      tenantId || null,
      user?.isTenantAdmin ?? false,
      user?.isGlobalAdmin ?? false,
      theme,
      readSidebarState()
    );
  }, [tenantId, user, theme]);

  return null;
}
