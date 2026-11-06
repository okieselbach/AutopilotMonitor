"use client";

/** A count of refusals: red when there were any, nothing otherwise. */
export function HitsPill({ count, title }: { count: number; title: string }) {
  if (count <= 0) return null;
  return (
    <span title={title} className="inline-flex shrink-0 items-center rounded-full bg-red-100 px-2 py-0.5 text-xs font-medium text-red-800">
      {count === 1 ? "1 limit hit" : `${count} limit hits`}
    </span>
  );
}
