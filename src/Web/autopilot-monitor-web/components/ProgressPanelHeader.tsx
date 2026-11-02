"use client";

import type { ReactNode } from "react";

interface ProgressPanelHeaderProps {
  title: string;
  icon: ReactNode;
  expanded: boolean;
  onToggle: () => void;
  // Summary text and pills after the title. May hold buttons of their own (the skipped and
  // pending toggles), which call stopPropagation so they do not also collapse the panel.
  children?: ReactNode;
}

/**
 * Collapsible head of the Download and Install panels. A plain div takes the mouse click so the
 * whole head stays a click target, and only the chevron is a real button: the pills hold buttons,
 * and a button inside a button is invalid HTML that hides the inner ones from screen readers.
 */
export default function ProgressPanelHeader({ title, icon, expanded, onToggle, children }: ProgressPanelHeaderProps) {
  return (
    <div onClick={onToggle} className="flex items-center justify-between w-full text-left cursor-pointer select-none">
      {/* flex-wrap + gap (not space-x): on narrow screens the pills wrap as whole units
          below the title instead of overflowing the viewport; whitespace-nowrap inherits
          so no pill breaks internally. */}
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 min-w-0 whitespace-nowrap">
        {icon}
        <h2 className="text-lg font-semibold text-gray-900">{title}</h2>
        {children}
      </div>
      <button
        onClick={(e) => { e.stopPropagation(); onToggle(); }}
        aria-expanded={expanded}
        aria-label={title}
        className="flex-shrink-0"
      >
        <svg className={`w-5 h-5 text-gray-400 flex-shrink-0 transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </button>
    </div>
  );
}
