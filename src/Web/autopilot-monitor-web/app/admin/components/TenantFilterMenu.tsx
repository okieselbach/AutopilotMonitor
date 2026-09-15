"use client";

import { useEffect, useRef, useState } from "react";
import {
  activeFilterCount,
  activeSelections,
  type FacetCounts,
  type FacetDefinition,
  type FacetKey,
  type TenantFilters,
} from "./tenantFilters";

interface TenantFilterMenuProps {
  facets: readonly FacetDefinition[];
  filters: TenantFilters;
  /** Faceted counts (see `facetCounts`): what each value would yield under the other facets. */
  counts: FacetCounts;
  onToggle: (key: FacetKey, value: string) => void;
  onClear: () => void;
}

/**
 * "Filter" button with a checkbox popover, one group per facet. Each option carries the
 * count it would match under the other active facets, so "Plan = Pro" immediately shows how
 * many of those are paying. Closes on outside click or Escape (same pattern as the
 * dashboard SettingsMenu).
 */
export function TenantFilterMenu({ facets, filters, counts, onToggle, onClear }: TenantFilterMenuProps) {
  const [open, setOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const active = activeFilterCount(filters);

  useEffect(() => {
    if (!open) return;
    const onMouseDown = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onMouseDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("mousedown", onMouseDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  return (
    <div className="relative" ref={menuRef}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-haspopup="true"
        className={`flex items-center gap-1.5 px-3 py-2 text-sm rounded-lg border transition-colors whitespace-nowrap ${
          active > 0
            ? "bg-green-600 text-white border-green-600"
            : "bg-white text-gray-700 border-gray-300 hover:bg-gray-50"
        }`}
      >
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 4h18l-7 8v6l-4 2v-8L3 4z" />
        </svg>
        <span>Filter</span>
        {active > 0 && (
          <span className="inline-flex items-center justify-center min-w-[1.25rem] h-5 px-1 rounded-full bg-white text-green-700 text-xs font-semibold">
            {active}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute right-0 mt-2 w-[min(40rem,calc(100vw-2rem))] bg-white rounded-lg shadow-lg border border-gray-200 z-50">
          <div className="p-4">
            <div className="flex items-center justify-between mb-3">
              <h3 className="text-sm font-semibold text-gray-900">Filter tenants</h3>
              {active > 0 && (
                <button type="button" onClick={onClear} className="text-xs text-gray-500 hover:text-gray-700 underline">
                  Clear all
                </button>
              )}
            </div>
            <div className="grid grid-cols-2 md:grid-cols-3 gap-x-6 gap-y-4">
              {facets.map((facet) => (
                <fieldset key={facet.key} className="min-w-0">
                  <legend className="text-xs font-semibold uppercase tracking-wide text-gray-500 mb-1.5">{facet.label}</legend>
                  <div className="space-y-1">
                    {facet.options.map((option) => {
                      const checked = filters[facet.key].has(option.value);
                      const count = counts[facet.key][option.value] ?? 0;
                      return (
                        <label
                          key={option.value}
                          className={`flex items-center gap-2 text-sm cursor-pointer rounded px-1 -mx-1 hover:bg-gray-50 ${
                            count === 0 && !checked ? "text-gray-400" : "text-gray-800"
                          }`}
                        >
                          <input
                            type="checkbox"
                            checked={checked}
                            onChange={() => onToggle(facet.key, option.value)}
                            className="rounded border-gray-300 text-green-600 focus:ring-green-500"
                          />
                          <span className="truncate">{option.label}</span>
                          <span className="ml-auto text-xs tabular-nums text-gray-500">{count}</span>
                        </label>
                      );
                    })}
                  </div>
                </fieldset>
              ))}
            </div>
            <p className="mt-3 text-xs text-gray-500">
              Values within a group combine with OR, groups with AND. Counts show what each value
              matches under the other groups.
            </p>
          </div>
        </div>
      )}
    </div>
  );
}

/** Removable chips for the active selections, rendered under the search row. */
export function TenantFilterChips({
  filters,
  onToggle,
  onClear,
}: {
  filters: TenantFilters;
  onToggle: (key: FacetKey, value: string) => void;
  onClear: () => void;
}) {
  const selections = activeSelections(filters);
  if (selections.length === 0) return null;
  return (
    <div className="flex flex-wrap items-center gap-2">
      {selections.map(({ key, option }) => (
        <button
          key={`${key}:${option.value}`}
          type="button"
          onClick={() => onToggle(key, option.value)}
          title={`Remove filter ${option.label}`}
          className={`inline-flex items-center gap-1 pl-2 pr-1.5 py-1 rounded-full text-xs font-medium ${option.badgeClass} hover:opacity-80 transition-opacity`}
        >
          {option.label}
          <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      ))}
      <button type="button" onClick={onClear} className="text-xs text-gray-500 hover:text-gray-700 underline">
        Clear all
      </button>
    </div>
  );
}
