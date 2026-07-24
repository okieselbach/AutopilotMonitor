"use client";

import { useState } from "react";

interface TruncatedLabelProps {
  /** The full text to display. Also used verbatim as the hover tooltip. */
  text: string;
  /** Extra classes for the text styling (font size, colour, padding). */
  className?: string;
  /**
   * When false, only the hover tooltip is provided — no click-to-expand and no
   * button semantics. Use this inside an already-clickable row/link/button,
   * where an inner onClick would hijack the outer navigation.
   */
  interactive?: boolean;
}

/**
 * A single-line label that truncates with an ellipsis by default.
 *
 * - Hovering always shows the full text in a native browser tooltip (standard
 *   tooltip, no special cursor).
 * - When `interactive` (the default), clicking — or pressing Enter/Space when
 *   focused — toggles a two-line expanded view. This keeps long values readable
 *   on touch devices where hover is unavailable.
 * - When `interactive={false}`, it is a plain truncated span with only the
 *   tooltip, safe to nest inside a clickable row or link.
 */
export default function TruncatedLabel({
  text,
  className = "",
  interactive = true,
}: TruncatedLabelProps) {
  const [expanded, setExpanded] = useState(false);

  if (!interactive) {
    return (
      <span title={text} className={`${className} min-w-0 truncate`}>
        {text}
      </span>
    );
  }

  const toggle = () => setExpanded((v) => !v);

  return (
    <span
      title={text}
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
      className={`${className} min-w-0 cursor-pointer ${
        expanded ? "line-clamp-2 break-words" : "truncate"
      }`}
    >
      {text}
    </span>
  );
}
