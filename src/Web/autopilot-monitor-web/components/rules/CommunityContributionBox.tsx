"use client";

import { DocsLink } from "@/components/DocsLink";
import { DOCS_PATHS } from "@/lib/docsPaths";

interface CommunityContributionBoxProps {
  /** Undefined hides the button (read-only roles, no custom rules yet). */
  onContribute?: () => void;
}

/** The community call-to-action on the rule pages: contribute a rule here, report bugs on GitHub. */
export function CommunityContributionBox({ onContribute }: CommunityContributionBoxProps) {
  return (
    <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 flex flex-col sm:flex-row sm:items-center gap-3">
      <div className="flex items-start space-x-3 flex-1">
        <svg className="w-5 h-5 text-blue-500 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
        <p className="text-sm text-blue-800">
          Built a rule that would help others? Contribute it to the community rule pool — you keep your copy and choose
          how you are credited (<DocsLink path={DOCS_PATHS.contributeRule} label="how it works" />). Bugs and ideas:{" "}
          <a href="https://github.com/okieselbach/AutopilotMonitor/issues" target="_blank" rel="noopener noreferrer" className="font-medium underline hover:text-blue-900">
            open a GitHub issue
          </a>.
        </p>
      </div>
      {onContribute && (
        <button
          onClick={onContribute}
          className="px-3 py-1.5 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors text-sm font-medium whitespace-nowrap flex-shrink-0"
        >
          Contribute a rule
        </button>
      )}
    </div>
  );
}
