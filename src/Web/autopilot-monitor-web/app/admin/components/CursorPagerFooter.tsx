"use client";

import type { CursorPager } from "@/hooks/useCursorPager";

type PagerControls = Pick<CursorPager<unknown>, "pageNumber" | "hasNext" | "hasPrev" | "next" | "prev">;

const BUTTON =
  "px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors";

/** Footer of the admin tables paged by the backend (Pattern B1): the row count, the page, Previous and Next. */
export function CursorPagerFooter({
  pager,
  count,
  loading,
  filtered = false,
}: {
  pager: PagerControls;
  count: number;
  loading: boolean;
  /** The count is a client-side filter of the page, not the page itself. */
  filtered?: boolean;
}) {
  return (
    <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md">
      <span className="text-xs text-gray-500 dark:text-gray-400">
        {count} on page {pager.pageNumber}{filtered ? " (filtered)" : ""}{pager.hasNext ? "" : " (last)"}
      </span>
      <div className="flex items-center gap-2">
        <button onClick={() => void pager.prev()} disabled={!pager.hasPrev || loading} className={BUTTON}>
          Previous
        </button>
        <button onClick={() => void pager.next()} disabled={!pager.hasNext || loading} className={BUTTON}>
          Next
        </button>
      </div>
    </div>
  );
}
