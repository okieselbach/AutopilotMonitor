// Server component — card-wrapped table skeleton with an optional title bar.
// Used by admin-area loading.tsx fallbacks (renders inside the persistent area
// sidebar layout) and by section components while their data loads.

import { Bar } from "./PageSkeleton";
import { TableSkeleton } from "./TableSkeleton";

interface SectionSkeletonProps {
  columns?: number;
  rows?: number;
  /** Render a title bar above the table. */
  title?: boolean;
  className?: string;
}

export function SectionSkeleton({
  columns = 6,
  rows = 8,
  title = true,
  className = "",
}: SectionSkeletonProps) {
  return (
    <div
      className={`rounded-lg border border-gray-200 bg-white shadow dark:border-gray-700 dark:bg-gray-800 ${className}`}
      aria-busy="true"
      aria-label="Loading"
    >
      {title && (
        <div className="border-b border-gray-200 p-6 dark:border-gray-700">
          <Bar className="h-5 w-48" />
          <Bar className="mt-3 h-3 w-72" />
        </div>
      )}
      <TableSkeleton columns={columns} rows={rows} />
    </div>
  );
}

export default SectionSkeleton;
