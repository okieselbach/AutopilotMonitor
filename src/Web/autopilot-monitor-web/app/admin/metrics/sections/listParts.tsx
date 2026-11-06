"use client";

import type { ReactNode } from "react";
import { editionLabel, facetOption, type PlanEdition } from "../../components/tenantFilters";
import type { SortDir } from "../lib/useListState";

/** Building blocks of the GA metrics lists (MCP Usage, Tenant Activity). */

/** A disclosure arrow that turns when its row opens. */
export function Chevron({ open }: { open: boolean }) {
  return (
    <svg
      className={`h-4 w-4 shrink-0 text-gray-400 transition-transform ${open ? "rotate-90" : ""}`}
      fill="none"
      stroke="currentColor"
      viewBox="0 0 24 24"
      aria-hidden="true"
    >
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
    </svg>
  );
}

/** A column label that sorts: ▲/▼ on the active column, ↕ on the others. */
export function SortHeader<K extends string>({
  sortKey,
  current,
  dir,
  onSort,
  children,
  align = "left",
}: {
  sortKey: K;
  current: K;
  dir: SortDir;
  onSort: (key: K) => void;
  children: ReactNode;
  align?: "left" | "right";
}) {
  const active = current === sortKey;
  return (
    <button
      type="button"
      onClick={() => onSort(sortKey)}
      className={`inline-flex items-center gap-1 text-xs font-medium uppercase tracking-wider text-gray-500 hover:text-gray-700 ${align === "right" ? "justify-end" : ""}`}
    >
      {children}
      <span className={`text-[10px] ${active ? "text-indigo-600" : "text-gray-300"}`} aria-hidden="true">
        {active ? (dir === "asc" ? "▲" : "▼") : "↕"}
      </span>
    </button>
  );
}

/** A neutral context label (role, plan, origin) in the gray-outline pill style. */
export function Pill({ children, title }: { children: ReactNode; title?: string }) {
  return (
    <span title={title} className="inline-flex shrink-0 items-center rounded border border-gray-300 bg-gray-100 px-1.5 py-0.5 text-xs font-medium text-gray-600">
      {children}
    </span>
  );
}

/** The tenant's edition in the colors the tenant lists use. */
export function EditionBadge({ edition }: { edition: PlanEdition }) {
  return (
    <span className={`inline-flex shrink-0 items-center rounded-full px-2 py-0.5 text-xs font-medium ${facetOption("plan", edition)?.badgeClass ?? ""}`}>
      {editionLabel(edition)}
    </span>
  );
}

/** A number with its own label below lg, where the column header row is hidden; amber for a figure worth a look. */
export function Figure({ label, value, tone }: { label: string; value: string; tone?: "warn" }) {
  return (
    <span className={`text-sm tabular-nums lg:text-right ${tone === "warn" ? "text-amber-700" : "text-gray-900"}`}>
      <span className="text-xs text-gray-500 lg:hidden">{label} </span>
      {value}
    </span>
  );
}

/** A grey line naming a gap in the data (a store that did not answer, a log that reaches back less far). */
export function DataNote({ children }: { children: ReactNode }) {
  return <p className="px-4 sm:px-6 py-2 text-xs text-gray-500 bg-gray-50 border-b border-gray-200">{children}</p>;
}

/**
 * The tenant column of an expandable list row: the disclosure arrow, the name (domain, else the id), the edition
 * badge and any extra pills, the company name below.
 */
export function TenantNameCell({
  open,
  name,
  edition,
  companyName,
  children,
}: {
  open: boolean;
  name: string;
  edition?: PlanEdition;
  companyName?: string;
  children?: ReactNode;
}) {
  return (
    <div className="flex min-w-0 basis-full items-start gap-2 lg:basis-auto">
      <span className="mt-0.5"><Chevron open={open} /></span>
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="font-medium text-gray-900 break-all sm:break-normal sm:truncate">{name}</span>
          {edition && <EditionBadge edition={edition} />}
          {children}
        </div>
        {companyName && <div className="text-xs text-gray-500 truncate">{companyName}</div>}
      </div>
    </div>
  );
}
