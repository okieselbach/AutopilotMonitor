"use client";

import type { ReactNode } from "react";
import type { GroupableRule, RuleCategoryGroup } from "@/lib/ruleGroups";

interface RuleCategoryGroupsProps<T extends GroupableRule> {
  groups: RuleCategoryGroup<T>[];
  /** Category tokens that are collapsed right now. */
  collapsed: ReadonlySet<string>;
  onToggleGroup: (category: string) => void;
  onExpandAll: () => void;
  onCollapseAll: () => void;
  /**
   * Keep every group open regardless of `collapsed` — used while a free-text search is
   * active so no hit disappears behind a closed group. Header and Collapse All are disabled.
   */
  forceExpanded?: boolean;
  /** True when a filter narrows the list; group headers then show "visible of total". */
  filtered: boolean;
  /** Left side of the toolbar row (e.g. "12 rules in 4 categories"). */
  summary: ReactNode;
  /** Pill colors for a category — the pages pass their existing category palette. */
  categoryColor: (category: string) => { bg: string; text: string };
  /** Stable DOM key for a rule (also the wrapper id prefix so a page can scroll to it). */
  ruleKey: (rule: T) => string;
  renderRule: (rule: T) => ReactNode;
}

/**
 * Rules grouped by category with a collapsible header per group and Expand/Collapse All.
 * The cards themselves are unchanged; only the wrapper structure is new.
 */
export function RuleCategoryGroups<T extends GroupableRule>({
  groups,
  collapsed,
  onToggleGroup,
  onExpandAll,
  onCollapseAll,
  forceExpanded = false,
  filtered,
  summary,
  categoryColor,
  ruleKey,
  renderRule,
}: RuleCategoryGroupsProps<T>) {
  return (
    <div className="space-y-4">
      {/* Toolbar */}
      <div className="flex items-center justify-between gap-2 px-1">
        <div className="flex items-center gap-2 min-w-0 text-sm text-gray-500">{summary}</div>
        <div className="flex gap-1.5 items-center flex-shrink-0">
          <button
            type="button"
            onClick={onExpandAll}
            disabled={forceExpanded || collapsed.size === 0}
            title="Expand all categories"
            className="flex items-center gap-1 px-2 py-1 text-xs bg-blue-50 text-blue-700 hover:bg-blue-100 rounded transition-colors disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:bg-blue-50"
          >
            <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
            </svg>
            <span className="hidden sm:inline">Expand All</span>
          </button>
          <button
            type="button"
            onClick={onCollapseAll}
            disabled={forceExpanded || groups.every((g) => collapsed.has(g.category))}
            title={forceExpanded ? "Categories stay expanded while searching" : "Collapse all categories"}
            className="flex items-center gap-1 px-2 py-1 text-xs bg-gray-50 text-gray-700 hover:bg-gray-100 rounded transition-colors disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:bg-gray-50"
          >
            <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 15l7-7 7 7" />
            </svg>
            <span className="hidden sm:inline">Collapse All</span>
          </button>
        </div>
      </div>

      {groups.map((group) => {
        const isOpen = forceExpanded || !collapsed.has(group.category);
        const color = categoryColor(group.category);
        const headerId = `rule-group-${group.category}`;
        const panelId = `rule-group-panel-${group.category}`;
        return (
          <section key={group.category} aria-labelledby={headerId}>
            <button
              type="button"
              id={headerId}
              onClick={() => onToggleGroup(group.category)}
              disabled={forceExpanded}
              aria-expanded={isOpen}
              aria-controls={panelId}
              title={forceExpanded ? "Categories stay expanded while searching" : isOpen ? "Collapse category" : "Expand category"}
              className="w-full flex items-center gap-2 px-1 py-1.5 text-left rounded hover:bg-gray-100 disabled:hover:bg-transparent disabled:cursor-default transition-colors select-none"
            >
              <svg
                className={`w-4 h-4 flex-shrink-0 text-gray-400 transition-transform ${isOpen ? "rotate-90" : ""}`}
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
              </svg>
              <span className={`px-2 py-0.5 rounded text-xs font-medium ${color.bg} ${color.text}`}>{group.label}</span>
              <span className="text-xs text-gray-500 truncate">
                {filtered ? `${group.rules.length} of ${group.total}` : group.total}{" "}
                {group.total === 1 ? "rule" : "rules"}
                {" · "}{group.active} active
                {group.custom > 0 && <>{" · "}{group.custom} custom</>}
              </span>
            </button>
            {isOpen && (
              <div id={panelId} className="space-y-3 mt-2">
                {group.rules.map((rule) => (
                  <div key={ruleKey(rule)} id={`rule-card-${ruleKey(rule)}`}>
                    {renderRule(rule)}
                  </div>
                ))}
              </div>
            )}
          </section>
        );
      })}
    </div>
  );
}
