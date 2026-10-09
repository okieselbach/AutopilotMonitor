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

const TABS = [
  { id: "installs", label: "Installs" },
  { id: "inventory", label: "Inventory" },
  { id: "vulnerabilities", label: "Vulnerabilities" },
] as const;
type TabId = (typeof TABS)[number]["id"];

function SoftwareHub() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const scope = useAggregatedAdminScope();
  // The window lives in the URL (?days=) and survives tab switches (selectTab keeps every parameter).
  const { days, ready: windowReady, setDays } = useWindowDays({ defaultDays: APPS_DEFAULT_WINDOW_DAYS, tenantId: scope.effectiveTenantId });

  const rawTab = searchParams.get("tab");
  const activeTab: TabId = TABS.some((t) => t.id === rawTab) ? (rawTab as TabId) : "installs";

  function selectTab(id: TabId) {
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
              </button>
            ))}
          </nav>
        </div>
      </header>

      <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
        {activeTab === "installs" && <InstallsTab scope={scope} days={days} windowReady={windowReady} />}
        {activeTab === "inventory" && <InventoryTab scope={scope} />}
        {activeTab === "vulnerabilities" && <VulnerabilitiesTab scope={scope} days={days} windowReady={windowReady} />}
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
