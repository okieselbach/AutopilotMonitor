"use client";

import { Suspense } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { useState } from "react";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAggregatedAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { SegmentedControl, TIME_RANGE_OPTIONS } from "@/components/SegmentedControl";
import InstallsTab from "./components/InstallsTab";
import InventoryTab from "./components/InventoryTab";
import VulnerabilitiesTab from "./components/VulnerabilitiesTab";
import type { TimeRange } from "./components/types";

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
  const [timeRange, setTimeRange] = useState<TimeRange>("30d");

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
                options={TIME_RANGE_OPTIONS}
                value={timeRange}
                onChange={(v) => setTimeRange(v as typeof timeRange)}
              />
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
        {activeTab === "installs" && <InstallsTab scope={scope} timeRange={timeRange} />}
        {activeTab === "inventory" && <InventoryTab scope={scope} />}
        {activeTab === "vulnerabilities" && <VulnerabilitiesTab scope={scope} timeRange={timeRange} />}
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
