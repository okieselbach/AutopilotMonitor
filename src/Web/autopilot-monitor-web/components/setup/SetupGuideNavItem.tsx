"use client";

import { useContinueSetup, useSetupChecklist } from "@/hooks/useSetupChecklist";

function RocketIcon({ className }: { className: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.75} aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" d="M15.59 14.37a6 6 0 01-5.84 7.38v-4.8m5.84-2.58a14.98 14.98 0 006.16-12.12A14.98 14.98 0 009.63 8.41m5.96 5.96a14.93 14.93 0 01-5.84 2.58m0 0a6 6 0 01-7.38-5.84h4.8m2.58 5.84a15 15 0 01-2.58-5.84m2.58 5.84l-2.58-5.84m0 0A14.98 14.98 0 019.63 8.41" />
    </svg>
  );
}

/**
 * Sidebar entry under the dashboard while the own tenant's setup checklist is open. It disappears
 * with the checklist: after the first enrollment or once an admin hides the guide.
 */
export function SetupGuideNavItem({ iconsOnly, onNavigate }: { iconsOnly: boolean; onNavigate: () => void }) {
  const { checklist } = useSetupChecklist();
  const continueSetup = useContinueSetup();
  if (!checklist?.visible) return null;

  const open = () => {
    onNavigate();
    continueSetup(checklist.next, "sidebar");
  };
  const progress = `${checklist.doneCount}/4`;
  const base = "flex items-center gap-2.5 rounded-md text-sm transition-colors text-green-700 hover:bg-green-50 dark:text-green-400 dark:hover:bg-green-900/20";

  if (iconsOnly) {
    return (
      <li>
        <button type="button" onClick={open} className={`${base} w-full px-2 py-2 justify-center relative group`} title={`Setup guide (${progress})`}>
          <span className="flex items-center justify-center w-full">
            <RocketIcon className="w-4.5 h-4.5" />
          </span>
          <span className="absolute top-1 right-1 h-1.5 w-1.5 rounded-full bg-green-500" aria-hidden="true" />
          <span className="absolute left-full ml-2 px-2 py-1 rounded bg-gray-900 text-white text-xs whitespace-nowrap opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity z-50 dark:bg-gray-700">
            Setup guide · {progress}
          </span>
        </button>
      </li>
    );
  }

  return (
    <li>
      <button type="button" onClick={open} className={`${base} w-full text-left px-3 py-1.5`}>
        <RocketIcon className="w-4 h-4" />
        <span className="truncate font-medium">Setup guide</span>
        <span className="ml-auto text-xs tabular-nums text-green-700/80 dark:text-green-400/80">{progress}</span>
      </button>
    </li>
  );
}
