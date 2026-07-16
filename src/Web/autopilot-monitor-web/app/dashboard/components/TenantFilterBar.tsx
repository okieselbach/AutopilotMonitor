"use client";

import { useEffect, useMemo, useRef, useState, useDeferredValue } from "react";

interface TenantFilterBarProps {
  tenantIdFilter: string;
  onChange: (value: string) => void;
  onSubmit: () => void;
  onClear: () => void;
  tenantList: { tenantId: string; domainName: string }[];
}

/**
 * Cross-tenant (Global Admin / delegated MSP) tenant filter input with fuzzy
 * suggestions. Extracted from SessionTable so the dashboard can keep it on
 * screen even when a filtered fetch returns zero sessions and the table itself
 * is not rendered — otherwise a GA drilling through tenants has no way to
 * clear or change the filter without leaving the page.
 */
export function TenantFilterBar({
  tenantIdFilter,
  onChange,
  onSubmit,
  onClear,
  tenantList,
}: TenantFilterBarProps) {
  const dropdownRef = useRef<HTMLDivElement>(null);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [selectedIndex, setSelectedIndex] = useState(-1);

  // Defer expensive suggestion scans so rapid typing keeps the input responsive.
  const deferredTenantIdFilter = useDeferredValue(tenantIdFilter);

  // Fuzzy-match tenants by domain name or tenant ID
  const suggestions = useMemo(() => {
    const q = deferredTenantIdFilter.trim().toLowerCase();
    if (q.length < 2 || tenantList.length === 0) return [];
    return tenantList
      .filter((t) => t.domainName.toLowerCase().includes(q) || t.tenantId.toLowerCase().includes(q))
      .slice(0, 8);
  }, [deferredTenantIdFilter, tenantList]);

  // Close suggestions on outside click
  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (dropdownRef.current && !dropdownRef.current.contains(e.target as Node)) {
        setShowSuggestions(false);
      }
    }
    if (showSuggestions) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => document.removeEventListener("mousedown", handleClickOutside);
    }
  }, [showSuggestions]);

  return (
    <div className="mb-3 flex items-center gap-2">
      <div className="relative flex-1" ref={dropdownRef}>
        <input
          type="text"
          placeholder="Filter by Tenant ID or domain name"
          value={tenantIdFilter}
          onChange={(e) => {
            onChange(e.target.value);
            setShowSuggestions(true);
            setSelectedIndex(-1);
          }}
          onFocus={() => {
            if (suggestions.length > 0) setShowSuggestions(true);
          }}
          onKeyDown={(e) => {
            if (showSuggestions && suggestions.length > 0) {
              if (e.key === "ArrowDown") {
                e.preventDefault();
                setSelectedIndex((i) => Math.min(i + 1, suggestions.length - 1));
                return;
              }
              if (e.key === "ArrowUp") {
                e.preventDefault();
                setSelectedIndex((i) => Math.max(i - 1, -1));
                return;
              }
              if (e.key === "Enter" && selectedIndex >= 0) {
                e.preventDefault();
                const selected = suggestions[selectedIndex];
                onChange(selected.tenantId);
                setShowSuggestions(false);
                setSelectedIndex(-1);
                return;
              }
              if (e.key === "Escape") {
                setShowSuggestions(false);
                return;
              }
            }
            if (e.key === "Enter") {
              setShowSuggestions(false);
              onSubmit();
            }
          }}
          className="w-full px-4 py-2 pr-10 border border-purple-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors font-mono text-sm"
        />
        {tenantIdFilter && (
          <button
            onClick={() => {
              onClear();
              setShowSuggestions(false);
            }}
            className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
            title="Clear tenant filter"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        )}
        {/* Tenant suggestions dropdown */}
        {showSuggestions && suggestions.length > 0 && (
          <div className="absolute z-50 mt-1 w-full bg-white border border-purple-200 rounded-lg shadow-lg max-h-64 overflow-y-auto">
            {suggestions.map((t, idx) => (
              <button
                key={t.tenantId}
                onClick={() => {
                  onChange(t.tenantId);
                  setShowSuggestions(false);
                  setSelectedIndex(-1);
                }}
                className={`w-full text-left px-4 py-2.5 flex flex-col gap-0.5 transition-colors ${
                  idx === selectedIndex
                    ? "bg-purple-100"
                    : "hover:bg-purple-50"
                }`}
              >
                <span className="text-sm font-medium text-gray-900">{t.domainName}</span>
                <span className="text-xs text-gray-500 font-mono">{t.tenantId}</span>
              </button>
            ))}
          </div>
        )}
      </div>
      <button
        onClick={() => {
          setShowSuggestions(false);
          onSubmit();
        }}
        className="px-4 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 transition-colors text-sm font-medium shrink-0"
      >
        Filter
      </button>
    </div>
  );
}
