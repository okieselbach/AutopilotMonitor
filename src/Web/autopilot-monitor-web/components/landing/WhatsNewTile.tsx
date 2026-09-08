"use client";

import { useWhatsNew } from "@/hooks/useWhatsNew";

/**
 * The "What's new" tile on the Help page. Same look as the link tiles around it, but a
 * button: it opens the What's new panel instead of leaving for the docs. No counter —
 * the public site has no seen mark to count against.
 */
export function WhatsNewTile({ className }: { className: string }) {
  const whatsNew = useWhatsNew();
  return (
    <button type="button" onClick={() => whatsNew.open("help-page")} className={`${className} text-left w-full`}>
      <span className="block text-sm font-semibold text-gray-800 mb-1">What&apos;s new →</span>
      <span className="block text-sm text-gray-600 leading-relaxed">
        What changed recently in the portal and the agent.
      </span>
    </button>
  );
}
