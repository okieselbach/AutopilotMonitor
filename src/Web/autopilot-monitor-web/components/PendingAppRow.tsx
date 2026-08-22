"use client";

import TruncatedLabel from "@/components/TruncatedLabel";

// Row for an app the tracker knows about (app_tracking_summary pendingNames) that has not
// produced any install/download event yet. Rendered behind the "N pending" toggle in the
// Download/Install Progress panels so their "X of Y" headers fully add up. Dashed border on
// purpose — this row is a placeholder from the tracker snapshot, not an observed lifecycle.
export default function PendingAppRow({ name }: { name: string }) {
  return (
    <div className="rounded-lg p-3 bg-gray-50 border border-dashed border-gray-300">
      <div className="flex items-center space-x-2 min-w-0">
        {/* Clock — waiting, nothing has happened yet */}
        <svg className="w-4 h-4 text-gray-400 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
        <TruncatedLabel text={name} className="text-sm font-medium text-gray-500" />
        <span
          className="text-xs px-2 py-0.5 rounded-full bg-gray-200 text-gray-600 font-medium"
          title="Assigned to this device but not picked up by the management extension yet — no install activity has been observed."
        >
          Pending
        </span>
      </div>
    </div>
  );
}
