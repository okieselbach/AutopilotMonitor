import { Bar } from "@/components/skeletons/PageSkeleton";
import { TableSkeleton } from "@/components/skeletons/TableSkeleton";

// Route-level fallback matching the Software Mapping page shell: header band +
// mapping card with table. Paints during navigation, before the client bundle
// hydrates and the unmatched-software fetch resolves.
export default function Loading() {
  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900" aria-busy="true" aria-label="Loading">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8 space-y-3">
          <Bar className="h-7 w-56" />
          <Bar className="h-3 w-80" />
        </div>
      </header>
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        <TableSkeleton columns={6} rows={10} wrapped />
      </main>
    </div>
  );
}
