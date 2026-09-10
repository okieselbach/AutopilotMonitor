"use client";

import { useGlobalAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { ReportingScopeProvider } from "./ReportingScopeContext";

export function ReportingSidebar({ children }: { children: React.ReactNode }) {
  // Override-only scope (always a concrete tenant, defaulting to the caller's own). A delegated (MSP)
  // caller gets neither banner nor selector here: the organization route never lists a managed
  // tenant's accounts, so the reporting sections stay on their own tenant for them.
  const scope = useGlobalAdminScope();
  const crossTenant = scope.isGlobalAdmin && !scope.isDelegatedScope;

  return (
    <ReportingScopeProvider value={scope}>
      <GlobalAdminBanner show={crossTenant} subtitle={globalAdminSubtitle(scope)} />
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <div className="flex flex-wrap items-center justify-between gap-y-3">
            <div>
              <h1 className="text-2xl font-normal text-gray-900">Reporting</h1>
              <p className="text-sm text-gray-500 mt-1">AI agent access and usage statistics</p>
            </div>
            {crossTenant && <TenantScopeSelector scope={scope} />}
          </div>
        </div>
      </header>
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </ReportingScopeProvider>
  );
}
