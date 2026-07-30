"use client";

export interface SegmentedOption<T extends string | number> {
  value: T;
  label: string;
}

/**
 * The one segmented toggle for time ranges, group-bys and friends —
 * connected pills on a gray track (the geographic-performance style),
 * replacing the mixed loose-buttons/dropdown variants.
 *
 * Dark mode: the active pill uses arbitrary-value colors on purpose —
 * the global `.dark .bg-white` override (#1e293b !important) would make
 * a `bg-white` pill invisible against the `bg-gray-100` track (same
 * mapped color).
 */
export function SegmentedControl<T extends string | number>({
  options,
  value,
  onChange,
  className = "",
}: {
  options: readonly SegmentedOption<T>[];
  value: T;
  onChange: (value: T) => void;
  className?: string;
}) {
  return (
    <div className={`flex bg-gray-100 rounded-lg p-1 ${className}`}>
      {options.map(option => (
        <button
          key={String(option.value)}
          onClick={() => onChange(option.value)}
          className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors whitespace-nowrap ${
            value === option.value
              ? "bg-[#ffffff] dark:bg-[#475569] text-gray-900 shadow-sm"
              : "text-gray-500 hover:text-gray-700 dark:hover:text-gray-300"
          }`}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

/** Shared 7/30/90-day options so every page shows identical labels. */
export const TIME_RANGE_OPTIONS = [
  { value: "7d", label: "7 Days" },
  { value: "30d", label: "30 Days" },
  { value: "90d", label: "90 Days" },
] as const;
