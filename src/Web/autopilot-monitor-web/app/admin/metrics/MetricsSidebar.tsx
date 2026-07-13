"use client";

export function MetricsSidebar({ children }: { children: React.ReactNode }) {
  return (
    <>
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Platform Metrics</h1>
          <p className="text-sm text-gray-500 mt-1">Platform-wide analytics</p>
        </div>
      </header>
      {/* Sections render full-bleed below the group header (like the standalone Geographic
          Performance page) so their min-h-screen loading/error gradients fill the whole content
          column instead of sitting boxed inside a max-w container. Each section owns its own
          inner max-w-7xl container. */}
      {children}
    </>
  );
}
