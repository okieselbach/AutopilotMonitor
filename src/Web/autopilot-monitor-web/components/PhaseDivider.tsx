import type { UserPhaseBoundary } from "@/lib/userPhaseBoundary";

// Quiet section marker for the progress panels. It names the Enrollment Status Page phase that
// was running when the rows below it started — a moment on the timeline, not the assignment:
// device-assigned scripts and apps run whenever the management extension syncs, so they show up
// under Account Setup too (routinely on Cloud PCs). Assignment is a separate pill on the row.
const TITLES = {
  before: (b: UserPhaseBoundary) =>
    `Started while the Enrollment Status Page was in ${b.beforeLabel}, before it entered ${b.afterLabel}.`,
  after: (b: UserPhaseBoundary) =>
    `Started after the Enrollment Status Page entered ${b.afterLabel}. This is when it ran, not how it is assigned — ` +
    "device-assigned scripts and apps run here as well whenever the management extension syncs.",
} as const;

export default function PhaseDivider({ boundary, side }: { boundary: UserPhaseBoundary; side: keyof typeof TITLES }) {
  const label = side === "before" ? boundary.beforeLabel : boundary.afterLabel;
  return (
    <div className="flex items-center gap-2" title={TITLES[side](boundary)} role="separator" aria-label={label}>
      <span className="text-[11px] font-medium uppercase tracking-wider text-gray-400 whitespace-nowrap">{label}</span>
      <div className="h-px flex-1 bg-gray-200" />
    </div>
  );
}
