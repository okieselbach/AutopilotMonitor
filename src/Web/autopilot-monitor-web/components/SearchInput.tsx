"use client";

/** Focus ring in the colour family of the card the field sits in. */
const FOCUS_TONES = {
  slate: "focus:ring-slate-500 focus:border-slate-500",
  amber: "focus:ring-amber-500 focus:border-amber-500",
} as const;

interface SearchInputProps {
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  /** The accessible name; a placeholder is not one. */
  ariaLabel: string;
  name: string;
  tone: keyof typeof FOCUS_TONES;
}

/**
 * The compact search field of the admin cards: a magnifier, a clear button while a query is set,
 * and Escape clears as well.
 */
export function SearchInput({ value, onChange, placeholder, ariaLabel, name, tone }: SearchInputProps) {
  return (
    <div className="relative">
      <svg className="absolute left-2.5 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
      </svg>
      <input
        type="text"
        name={name}
        placeholder={placeholder}
        aria-label={ariaLabel}
        autoComplete="off"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape" && value) onChange("");
        }}
        className={`w-52 pl-8 pr-7 py-1 text-xs border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 ${FOCUS_TONES[tone]}`}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange("")}
          title="Clear search"
          aria-label="Clear search"
          className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
        >
          <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      )}
    </div>
  );
}
