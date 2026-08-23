"use client";

import { useState } from "react";
import type { DiagnosticsBuiltInSection } from "@/types/diagnostics";
import { ContextPill, DiagnosticsPathRow } from "./DiagnosticsPathRow";
import { describeCondition, formatBuiltInSection } from "./builtInSectionDisplay";

interface BuiltInSectionsListProps {
  sections: DiagnosticsBuiltInSection[];
  loading?: boolean;
  /**
   * Tenant view: the tenant's PERSISTED RealmJoin Watcher toggle, so the RealmJoin rows show
   * on/off. Global-Admin view: undefined — there is no tenant context, the pill stays neutral.
   */
  realmJoinWatcherEnabled?: boolean;
}

function StateDot({ on }: { on: boolean }) {
  return <span className={`h-1.5 w-1.5 rounded-full ${on ? "bg-green-500" : "bg-gray-300 dark:bg-gray-500"}`} />;
}

/**
 * The sections compiled into the agent — collected by every diagnostics package before any
 * configured path. Collapsed by default so the list never sits between the administrator and
 * the add-row; the header still carries the count and the RealmJoin state.
 */
export function BuiltInSectionsList({ sections, loading = false, realmJoinWatcherEnabled }: BuiltInSectionsListProps) {
  const [expanded, setExpanded] = useState(false);
  const toggle = () => setExpanded((v) => !v);
  const hasRealmJoin = sections.some((s) => s.condition === "RealmJoinWatcher");

  return (
    <div className="mb-3">
      {/* div[role=button] instead of <button>: a read-only viewer's ReadOnlyFieldset disables
          every nested button, but expanding a read-only list must keep working there. */}
      <div
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onClick={toggle}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            toggle();
          }
        }}
        className="flex cursor-pointer select-none items-center gap-2 py-1 text-xs font-medium uppercase tracking-wide text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
      >
        <svg
          className={`h-3.5 w-3.5 flex-shrink-0 transition-transform ${expanded ? "rotate-90" : ""}`}
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
        <span>Built-in</span>
        <ContextPill className="normal-case tracking-normal">
          {loading ? "loading…" : `${sections.length} sections`}
        </ContextPill>
        {hasRealmJoin && realmJoinWatcherEnabled !== undefined && (
          <ContextPill
            className="normal-case tracking-normal"
            title="RealmJoin sections are collected only while the RealmJoin Watcher is enabled (Settings → Agent)."
          >
            <StateDot on={realmJoinWatcherEnabled} />
            RealmJoin {realmJoinWatcherEnabled ? "on" : "off"}
          </ContextPill>
        )}
      </div>

      {expanded && (
        <div className="mt-1.5 space-y-1">
          <p className="text-xs text-gray-500 dark:text-gray-400">
            Collected by every agent before any configured path. Compiled into the agent — not editable here.
          </p>
          {!loading && sections.length === 0 && (
            <p className="text-xs italic text-gray-400">Catalog unavailable.</p>
          )}
          {sections.map((s) => {
            const display = formatBuiltInSection(s);
            const condition = describeCondition(s.condition, realmJoinWatcherEnabled);
            return (
              <DiagnosticsPathRow
                key={s.id}
                path={display.pathText}
                title={`${s.sourceFolder} → ${s.zipFolder}/`}
                description={s.description}
                includeSubfolders={s.includeSubfolders}
                pills={
                  <>
                    {display.patternSummary && (
                      // Secondary detail: hidden on narrow screens so the path keeps its width.
                      <ContextPill title={display.patternTitle} className="hidden sm:inline-flex">
                        {display.patternSummary}
                      </ContextPill>
                    )}
                    {condition && (
                      <ContextPill title={condition.title}>
                        {condition.state !== undefined && <StateDot on={condition.state} />}
                        {condition.label}
                      </ContextPill>
                    )}
                  </>
                }
              />
            );
          })}
        </div>
      )}
    </div>
  );
}
