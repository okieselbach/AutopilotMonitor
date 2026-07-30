"use client";

import { useMemo } from "react";
import { usePageSections } from "../../hooks/usePageSections";
import { PageSectionItem } from "../../contexts/SidebarContext";
import { route } from "../../lib/routes";
import {
  GearIcon,
  DocumentTextIcon,
  ChartBarIcon,
  BuildingOfficeIcon,
  ShieldCheckIcon,
} from "../../lib/sidebarIcons";

// Inline icon for Ops (wrench)
function WrenchIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M11.42 15.17L17.25 21A2.652 2.652 0 0021 17.25l-5.877-5.877M11.42 15.17l2.496-3.03c.317-.384.74-.626 1.208-.766M11.42 15.17l-4.655 5.653a2.548 2.548 0 11-3.586-3.586l6.837-5.63m5.108-.233c.55-.164 1.163-.188 1.743-.14a4.5 4.5 0 004.486-6.336l-3.276 3.277a3.004 3.004 0 01-2.25-2.25l3.276-3.276a4.5 4.5 0 00-6.336 4.486c.091 1.076-.071 2.264-.904 2.95l-.102.085m-1.745 1.437L5.909 7.5H4.5L2.25 3.75l1.5-1.5L7.5 4.5v1.409l4.26 4.26m-1.745 1.437l1.745-1.437" />
    </svg>
  );
}

// Inline icon for Software (globe)
function GlobeIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M12 21a9.004 9.004 0 008.716-6.747M12 21a9.004 9.004 0 01-8.716-6.747M12 21c2.485 0 4.5-4.03 4.5-9S14.485 3 12 3m0 18c-2.485 0-4.5-4.03-4.5-9S9.515 3 12 3m0 0a8.997 8.997 0 017.843 4.582M12 3a8.997 8.997 0 00-7.843 4.582m15.686 0A11.953 11.953 0 0112 10.5c-2.998 0-5.74-1.1-7.843-2.918m15.686 0A8.959 8.959 0 0121 12c0 .778-.099 1.533-.284 2.253m0 0A17.919 17.919 0 0112 16.5c-3.162 0-6.133-.815-8.716-2.247m0 0A9.015 9.015 0 013 12c0-1.605.42-3.113 1.157-4.418" />
    </svg>
  );
}

/**
 * Registers all admin sections in the global sidebar
 * with group information for expandable rendering.
 * Placed inside AdminConfigProvider in the admin layout.
 */
export function AdminPageSections() {
  const items: PageSectionItem[] = useMemo(() => [
    // Tenants
    { id: "management", label: "Tenant Management", href: route("/admin/tenants/management"), group: "Tenants", groupIcon: <BuildingOfficeIcon /> },
    { id: "config-report", label: "Config Report", href: route("/admin/tenants/config-report"), group: "Tenants" },

    // Metrics
    { id: "platform-metrics", label: "Platform Metrics", href: route("/admin/metrics/platform-metrics"), group: "Metrics", groupIcon: <ChartBarIcon /> },
    { id: "usage", label: "Platform Usage", href: route("/admin/metrics/usage"), group: "Metrics" },
    { id: "mcp-usage", label: "MCP Usage", href: route("/admin/metrics/mcp-usage"), group: "Metrics" },

    // Reports
    { id: "session-reports", label: "Session Reports", href: route("/admin/reports/session-reports"), group: "Reports", groupIcon: <DocumentTextIcon /> },
    { id: "user-feedback", label: "User Feedback", href: route("/admin/reports/user-feedback"), group: "Reports" },
    { id: "session-export", label: "Session Export", href: route("/admin/reports/session-export"), group: "Reports" },

    // Security
    { id: "device-block", label: "Device Block", href: route("/admin/security/device-block"), group: "Security", groupIcon: <ShieldCheckIcon /> },
    { id: "version-block", label: "Version Block", href: route("/admin/security/version-block"), group: "Security" },
    { id: "vulnerability-data", label: "Vulnerability Data", href: route("/admin/security/vulnerability-data"), group: "Security" },

    // Settings
    { id: "global", label: "Global Settings", href: route("/admin/settings/global"), group: "Settings", groupIcon: <GearIcon /> },
    { id: "diagnostics-log-paths", label: "Diagnostics Log Paths", href: route("/admin/settings/diagnostics-log-paths"), group: "Settings" },
    { id: "config-reseed", label: "Config Reseed", href: route("/admin/settings/config-reseed"), group: "Settings" },
    { id: "usage-plans", label: "Usage Plans", href: route("/admin/settings/usage-plans"), group: "Settings" },

    // Ops (single page)
    { id: "ops", label: "Maintenance", href: "/admin/ops", group: "Ops", groupIcon: <WrenchIcon /> },

    // Software (single page)
    { id: "software", label: "Software Mapping", href: "/admin/software", group: "Software", groupIcon: <GlobeIcon /> },
  ], []);

  usePageSections(items, "Global Admin", "route");

  return null;
}
