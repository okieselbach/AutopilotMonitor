"use client";

import EditionBadge from "../components/EditionBadge";

export function TenantSidebar({ children }: { children: React.ReactNode }) {
  return (
    <>
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-normal text-gray-900">Tenant Configuration</h1>
            <p className="text-sm text-gray-500 mt-1">Validation, notifications, and access management</p>
          </div>
          <EditionBadge />
        </div>
      </header>
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
