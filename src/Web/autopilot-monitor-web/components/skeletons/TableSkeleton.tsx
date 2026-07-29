// Server component (no "use client", no hooks) so it can serve both as part of
// a route-level loading.tsx fallback and as the in-page data-slot placeholder
// while a client component fetches.
//
// Loading-UX convention (applies to every page with fetched table data):
//   1. Never early-return the whole page on `loading` — render the page shell
//      and put this skeleton in the data slot only.
//   2. Any loading UI must live INSIDE <ProtectedRoute>; an early return above
//      it hangs forever on a fresh tab because the MSAL redirect never fires
//      (see the comment in app/diagnosis/page.tsx).
//   3. Show the skeleton only when `loading && items.length === 0` so a
//      filter-driven refetch keeps the populated table on screen.
//   4. Initialise `loading` to true when a fetch fires on mount, otherwise the
//      empty state flashes for one paint before the fetch starts.

import { Bar } from "./PageSkeleton";

interface TableSkeletonProps {
  /** Number of columns to render. */
  columns?: number;
  /** Number of placeholder body rows. */
  rows?: number;
  /** Render a shimmer header row. */
  header?: boolean;
  /** Wrap in the standard card chrome (rounded border + bg + shadow). */
  wrapped?: boolean;
  className?: string;
}

// Deterministic pseudo-random widths so rows look organic without hydration
// mismatches (no Math.random in render).
const WIDTHS = ["w-24", "w-16", "w-32", "w-20", "w-28", "w-12", "w-36"];

export function TableSkeleton({
  columns = 5,
  rows = 8,
  header = true,
  wrapped = false,
  className = "",
}: TableSkeletonProps) {
  const table = (
    <div className="overflow-x-auto" aria-busy="true" aria-label="Loading">
      <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
        {header && (
          <thead className="bg-gray-50 dark:bg-gray-900/40">
            <tr>
              {Array.from({ length: columns }).map((_, c) => (
                <th key={c} className="px-4 py-3 text-left">
                  <Bar className="h-3 w-16" />
                </th>
              ))}
            </tr>
          </thead>
        )}
        <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
          {Array.from({ length: rows }).map((_, r) => (
            <tr key={r}>
              {Array.from({ length: columns }).map((_, c) => (
                <td key={c} className="px-4 py-3.5">
                  <Bar className={`h-4 ${WIDTHS[(r * columns + c) % WIDTHS.length]}`} />
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );

  if (!wrapped) {
    return className ? <div className={className}>{table}</div> : table;
  }
  return (
    <div
      className={`rounded-lg border border-gray-200 bg-white shadow dark:border-gray-700 dark:bg-gray-800 ${className}`}
    >
      {table}
    </div>
  );
}

export default TableSkeleton;
