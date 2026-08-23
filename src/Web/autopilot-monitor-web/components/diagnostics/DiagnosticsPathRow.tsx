"use client";

import type { ReactNode } from "react";
import { ValidationIndicator } from "@/components/ValidationIndicator";
import type { ValidationResult } from "@/utils/guardValidation";

/** Neutral gray outline pill for context labels (scope, file types, collection gate) — never a state colour. */
export function ContextPill({
  children,
  title,
  className = "",
}: {
  children: ReactNode;
  title?: string;
  className?: string;
}) {
  return (
    <span
      title={title}
      className={`inline-flex flex-shrink-0 items-center gap-1 whitespace-nowrap rounded-full border border-gray-300 dark:border-gray-600 px-1.5 py-0.5 text-[10px] leading-none text-gray-600 dark:text-gray-300 ${className}`}
    >
      {children}
    </span>
  );
}

interface DiagnosticsPathRowProps {
  path: string;
  /** Tooltip for the path; defaults to the path itself. */
  title?: string;
  description?: string;
  includeSubfolders: boolean;
  /** Present ⇒ editable row with a compact "subfolders" checkbox. Absent ⇒ read-only "recursive" pill when true. */
  onToggleSubfolders?: () => void;
  /** Present ⇒ remove (×) button. */
  onRemove?: () => void;
  /** Agent-side guard verdict for configured entries. Never pass for built-in sections (they are code, not config). */
  validation?: ValidationResult | null;
  /** Trailing context pills. */
  pills?: ReactNode;
  /** Card tint (background + border); defaults to neutral gray. */
  className?: string;
  /** Path text colour. */
  pathClassName?: string;
}

/**
 * One diagnostics path per row. From `sm` up everything sits on ONE line — path (truncated,
 * full value in the tooltip), description, guard verdict, context pills, subfolder control,
 * remove — so long lists stay scannable and the add-row above them stays within reach.
 * Below `sm` the path owns the first line(s) and wraps in full (a phone cannot hover a
 * tooltip, and a path cut to "C:\…" is useless); badges and controls flow onto the next line.
 */
export function DiagnosticsPathRow({
  path,
  title,
  description,
  includeSubfolders,
  onToggleSubfolders,
  onRemove,
  validation,
  pills,
  className = "bg-gray-50 dark:bg-gray-800/60 border-gray-200 dark:border-gray-700",
  pathClassName = "text-gray-700 dark:text-gray-200",
}: DiagnosticsPathRowProps) {
  return (
    <div className={`flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 rounded-md border px-2.5 py-1 sm:flex-nowrap ${className}`}>
      <span
        className={`min-w-0 basis-full break-all font-mono text-xs sm:flex-1 sm:basis-auto sm:truncate sm:break-normal ${pathClassName}`}
        title={title ?? path}
      >
        {path}
      </span>
      {description && (
        <span
          className="hidden min-w-0 max-w-[40%] truncate text-xs text-gray-500 dark:text-gray-400 sm:inline"
          title={description}
        >
          {description}
        </span>
      )}
      {validation !== undefined && <ValidationIndicator result={validation} className="flex-shrink-0" />}
      {pills}
      {onToggleSubfolders ? (
        <label className="flex flex-shrink-0 cursor-pointer items-center gap-1 whitespace-nowrap text-xs text-gray-500 dark:text-gray-400">
          <input
            type="checkbox"
            checked={includeSubfolders}
            onChange={onToggleSubfolders}
            className="h-3.5 w-3.5 rounded border-gray-400 text-green-600 focus:ring-green-500"
          />
          subfolders
        </label>
      ) : includeSubfolders ? (
        <ContextPill title="Subfolders are collected recursively">recursive</ContextPill>
      ) : null}
      {onRemove && (
        <button
          type="button"
          onClick={onRemove}
          className="flex-shrink-0 text-gray-400 transition-colors hover:text-red-600 dark:hover:text-red-400"
          title="Remove"
          aria-label={`Remove ${path}`}
        >
          <svg className="h-3.5 w-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      )}
    </div>
  );
}
