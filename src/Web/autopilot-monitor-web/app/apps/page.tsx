"use client";

import { Suspense } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAggregatedAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { SegmentedControl } from "@/components/SegmentedControl";
import { WINDOW_PRESET_OPTIONS } from "@/lib/timeWindow";
import { useWindowDays } from "@/hooks/useWindowDays";
import InstallsTab from "./components/InstallsTab";
import { APPS_DEFAULT_WINDOW_DAYS } from "./components/types";
import InventoryTab from "./components/InventoryTab";
import VulnerabilitiesTab from "./components/VulnerabilitiesTab";
import { DocsLink } from "@/components/DocsLink";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import { trackEvent } from "@/lib/appInsights";
import { canChangeOwnTenantSettings } from "@/lib/tenantScope";
import { useVulnerabilityHeadline } from "@/hooks/useVulnerabilityHeadline";

const TABS = [
  { id: "installs", label: "Installs" },
  { id: "inventory", label: "Inventory" },
  { id: "vulnerabilities", label: "Vulnerabilities" },
] as const;
type TabId = (typeof TABS)[number]["id"];

function SoftwareHub() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const { user } = useAuth();
  const { tenantId: ownTenantId } = useTenant();
  // ?tenantId= (dashboard card, session report) points a Global Admin at that tenant on first load.
  const scope = useAggregatedAdminScope({ urlTenantId: searchParams.get("tenantId") ?? "" });
  // The window lives in the URL (?days=) and survives tab switches (selectTab keeps every parameter).
  const { days, ready: windowReady, setDays } = useWindowDays({ defaultDays: APPS_DEFAULT_WINDOW_DAYS, tenantId: scope.effectiveTenantId });

  // Analyzer switch and known-exploited count of the tenant in view: the count goes on the
  // Vulnerabilities tab label, the switch decides both tabs' empty state. Only the active tab is
  // mounted, so the page reads them itself. The aggregated Global Admin view (the slow cross-tenant
  // scan) reads nothing.
  const singleTenant = scope.scopeInitialized && windowReady && !scope.isAggregatedGlobalView && !!scope.effectiveTenantId;
  const headline = useVulnerabilityHeadline(singleTenant ? scope : null, singleTenant ? scope.effectiveTenantId : null, days);
  const kev = headline.status === "ready" ? headline.kev : 0;
  // undefined = no single tenant or not readable; null = still loading.
  const analyzerOn = headline.status === "ready" ? headline.analyzerOn : headline.status === "loading" ? null : undefined;
  const canEnable = canChangeOwnTenantSettings(user, scope.effectiveTenantId, ownTenantId);

  const rawTab = searchParams.get("tab");
  const activeTab: TabId = TABS.some((t) => t.id === rawTab) ? (rawTab as TabId) : "installs";

  function selectTab(id: TabId) {
    trackEvent("software_tab_selected", { tab: id, badge: kev > 0, kev });
    const params = new URLSearchParams(searchParams.toString());
    params.set("tab", id);
    router.replace(`/apps?${params.toString()}`);
  }

  return (
    <div className="min-h-screen bg-gray-50">
      <GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} subtitle={globalAdminSubtitle(scope)} />
      <header className="bg-white shadow">
        <div className="max-w-7xl mx-auto pt-6 px-4 sm:px-6 lg:px-8">
          <div className="flex flex-wrap items-center justify-between gap-y-3">
            <div>
              <h1 className="text-2xl font-normal text-gray-900">Software</h1>
              <p className="text-sm text-gray-500 mt-1">
                App installs, the installed-software inventory, and vulnerability exposure across enrollments.
              </p>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <TenantScopeSelector scope={scope} allowAggregated />
              <SegmentedControl
                options={WINDOW_PRESET_OPTIONS}
                value={days}
                onChange={setDays}
              />
              <DocsLink path={DOCS_PATHS.softwareInventory} label="Docs" />
            </div>
          </div>

          {/* Lens tabs — installs / inventory / vulnerabilities, synced to ?tab= */}
          <nav className="mt-4 flex gap-6 border-b border-gray-200">
            {TABS.map((t) => (
              <button
                key={t.id}
                onClick={() => selectTab(t.id)}
                className={`-mb-px border-b-2 px-1 py-3 text-sm font-medium transition-colors ${
                  activeTab === t.id
                    ? "border-green-600 text-green-700"
                    : "border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300"
                }`}
              >
                {t.label}
                {t.id === "vulnerabilities" && kev > 0 && (
                  <span className="ml-2 inline-flex items-center rounded-full bg-red-100 px-2 py-0.5 text-xs font-semibold text-red-700">
                    <span aria-hidden="true">{kev.toLocaleString()}</span>
                    <span className="sr-only">{`${kev.toLocaleString()} known exploited`}</span>
                  </span>
                )}
              </button>
            ))}
          </nav>
        </div>
      </header>

      <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
        {activeTab === "installs" && <InstallsTab scope={scope} days={days} windowReady={windowReady} />}
        {activeTab === "inventory" && <InventoryTab scope={scope} analyzerOn={analyzerOn} canEnable={canEnable} />}
        {activeTab === "vulnerabilities" && (
          <VulnerabilitiesTab scope={scope} days={days} windowReady={windowReady} analyzerOn={analyzerOn} canEnable={canEnable} />
        )}
      </main>
    </div>
  );
}

export default function SoftwarePage() {
  return (
    <ProtectedRoute>
      {/* useSearchParams requires a Suspense boundary under the App Router. */}
      <Suspense fallback={<div className="min-h-screen bg-gray-50" />}>
        <SoftwareHub />
      </Suspense>
    </ProtectedRoute>
  );
}
