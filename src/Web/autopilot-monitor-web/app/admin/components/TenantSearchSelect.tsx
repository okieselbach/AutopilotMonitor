"use client";

import { useState, useRef, useEffect, useMemo } from "react";
import { TenantConfiguration } from "./TenantManagementSection";

interface TenantSearchSelectProps {
  tenants: TenantConfiguration[];
  value: string;
  onChange: (tenantId: string) => void;
  placeholder?: string;
  focusRingClass?: string;
}

export function TenantSearchSelect({
  tenants,
  value,
  onChange,
  placeholder = "Search tenant by name or ID\u2026",
  focusRingClass = "focus:ring-green-500 focus:border-green-500",
}: TenantSearchSelectProps) {
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Resolve the selected tenant for display
  const selectedTenant = tenants.find((t) => t.tenantId === value);

  const filtered = useMemo(() => {
    if (!query) return tenants;
    const q = query.toLowerCase();
    return tenants.filter(
      (t) =>
        t.domainName?.toLowerCase().includes(q) ||
        t.tenantId.toLowerCase().includes(q)
    );
  }, [tenants, query]);

  // Close dropdown on click outside
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);

  const handleSelect = (tenantId: string) => {
    onChange(tenantId);
    setQuery("");
    setOpen(false);
  };

  const handleClear = () => {
    onChange("");
    setQuery("");
    setOpen(false);
    inputRef.current?.focus();
  };

  const handleFocus = () => {
    setOpen(true);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") {
      setOpen(false);
      inputRef.current?.blur();
    }
  };

  const label = (t: TenantConfiguration) =>
    t.domainName ? `${t.domainName} (${t.tenantId})` : t.tenantId;

  return (
    <div ref={containerRef} className="relative">
      <div className="relative">
        <svg
          className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400 pointer-events-none"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
          />
        </svg>
        <input
          ref={inputRef}
          type="text"
          value={open ? query : selectedTenant ? label(selectedTenant) : query}
          onChange={(e) => {
            setQuery(e.target.value);
            if (!open) setOpen(true);
          }}
          onFocus={handleFocus}
          onKeyDown={handleKeyDown}
          placeholder={selectedTenant ? label(selectedTenant) : placeholder}
          className={`w-full pl-9 pr-8 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 ${focusRingClass}`}
        />
        {value && (
          <button
            type="button"
            onClick={handleClear}
            className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
            tabIndex={-1}
          >
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        )}
      </div>

      {open && (
        <ul className="absolute z-50 mt-1 w-full max-h-52 overflow-auto bg-white dark:bg-gray-800 border border-gray-300 dark:border-gray-600 rounded-lg shadow-lg">
          {filtered.length === 0 ? (
            <li className="px-3 py-2 text-sm text-gray-500 dark:text-gray-400 italic">
              No tenants found
            </li>
          ) : (
            filtered.map((t) => (
              <li
                key={t.tenantId}
                onMouseDown={() => handleSelect(t.tenantId)}
                className={`px-3 py-2 text-sm cursor-pointer hover:bg-gray-100 dark:hover:bg-gray-700 ${
                  t.tenantId === value
                    ? "bg-gray-100 dark:bg-gray-700 font-medium"
                    : "text-gray-900 dark:text-gray-100"
                }`}
              >
                {t.domainName ? (
                  <>
                    <span className="font-medium">{t.domainName}</span>
                    <span className="ml-1.5 text-gray-400 dark:text-gray-500 text-xs">{t.tenantId}</span>
                  </>
                ) : (
                  <span className="font-mono">{t.tenantId}</span>
                )}
              </li>
            ))
          )}
        </ul>
      )}
    </div>
  );
}
