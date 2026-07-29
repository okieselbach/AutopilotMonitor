// Server component (no "use client") so App Router can stream it as a route
// Suspense fallback during navigation — it paints instantly, before the
// destination's client bundle downloads and hydrates. Shared shell used by the
// per-route loading.tsx files.

export function Bar({ className = "" }: { className?: string }) {
  return (
    <div
      className={`animate-pulse rounded bg-gray-200 dark:bg-gray-700 ${className}`}
    />
  );
}

export function CardSkeleton() {
  return (
    <div className="rounded-lg border border-gray-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
      <Bar className="h-3 w-24" />
      <Bar className="mt-4 h-8 w-20" />
      <Bar className="mt-3 h-2 w-32" />
    </div>
  );
}

interface PageSkeletonProps {
  /** Number of metric cards to render in the stats grid. */
  cards?: number;
  /** Number of placeholder rows in the list/table block. */
  rows?: number;
  /** Render the wide chart/table block below the cards. */
  showBlock?: boolean;
}

export function PageSkeleton({
  cards = 4,
  rows = 6,
  showBlock = true,
}: PageSkeletonProps) {
  return (
    <div
      className="mx-auto max-w-[1400px] space-y-6 p-6"
      aria-busy="true"
      aria-label="Loading"
    >
      {/* Header */}
      <div className="space-y-3">
        <Bar className="h-7 w-56" />
        <Bar className="h-3 w-80" />
      </div>

      {/* Stat cards */}
      {cards > 0 && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {Array.from({ length: cards }).map((_, i) => (
            <CardSkeleton key={i} />
          ))}
        </div>
      )}

      {/* List / chart block */}
      {showBlock && (
        <div className="rounded-lg border border-gray-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
          <Bar className="h-4 w-40" />
          <div className="mt-5 space-y-3">
            {Array.from({ length: rows }).map((_, i) => (
              <Bar key={i} className="h-9 w-full" />
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

export default PageSkeleton;
