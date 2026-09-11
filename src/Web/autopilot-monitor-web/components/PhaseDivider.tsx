// Quiet section marker for the progress panels: rows above started in the device part of the
// enrollment, rows below after the device entered Account Setup (see lib/userPhaseBoundary).
const PHASES = {
  device: {
    label: "Device phase",
    title: "Started before the device entered Account Setup — the device part of the Enrollment Status Page.",
  },
  user: {
    label: "User phase",
    title: "Started after the device entered Account Setup — the user part of the Enrollment Status Page.",
  },
} as const;

export default function PhaseDivider({ phase }: { phase: keyof typeof PHASES }) {
  const { label, title } = PHASES[phase];
  return (
    <div className="flex items-center gap-2" title={title} role="separator" aria-label={label}>
      <span className="text-[11px] font-medium uppercase tracking-wider text-gray-400 whitespace-nowrap">{label}</span>
      <div className="h-px flex-1 bg-gray-200" />
    </div>
  );
}
