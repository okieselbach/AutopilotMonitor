import { Reveal } from "./Reveal";

const ROWS = [
  {
    label: "Deployment visibility",
    monitor: "Real-time phase tracking with live push updates",
    standard: "None — black box until it finishes or fails",
  },
  {
    label: "Download progress",
    monitor: "Per-app download speed, bytes transferred, % complete",
    standard: "No visibility into what's downloading or how long it takes",
  },
  {
    label: "User-facing progress page",
    monitor: "Progress view with live app status & download info",
    standard: "Generic ESP screen — no details for the end user",
  },
  {
    label: "Fleet health dashboard",
    monitor: "Success rates, failure trends, avg. duration across all devices",
    standard: "Limited manual report extraction from Intune",
  },
  {
    label: "Analyze rules",
    monitor: "Built-in + fully customizable rules for automated issue detection",
    standard: "Manual log review required after every failed deployment",
  },
  {
    label: "Extended data gathering",
    monitor: "Custom gather rules to capture registry, files, or WMI on any event",
    standard: "No automated data collection during enrollment",
  },
  {
    label: "Geo & network context",
    monitor: "Device location, and network info captured at enrollment start",
    standard: "No location or network context in deployment records",
  },
  {
    label: "Performance monitoring",
    monitor: "CPU, memory, disk, and network snapshots during deployment",
    standard: "Not captured — no way to detect resource bottlenecks",
  },
  {
    label: "Troubleshooting speed",
    monitor: "Drill into per-event timeline, IME log patterns, and analyze results",
    standard: "Manual IME log hunting — slow and error-prone",
  },
];

export function Comparison() {
  return (
    <section id="comparison" className="py-20 sm:py-24 px-6 scroll-mt-20">
      <div className="max-w-7xl mx-auto">
        <Reveal className="max-w-2xl mx-auto text-center">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Compare</p>
          <h2 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)]">
            Standard Autopilot vs. monitored Autopilot
          </h2>
          <p className="mt-4 text-[var(--lp-ink-soft)]">
            What you&apos;re missing without Autopilot Monitor — and what you gain the moment you deploy it.
          </p>
        </Reveal>

        <Reveal className="mt-12">
          <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-xl shadow-black/[0.05] overflow-hidden">
            {/* Column header */}
            <div className="hidden md:grid grid-cols-[220px_1fr_1fr] border-b border-[var(--lp-line)]">
              <div />
              <div className="px-5 py-4 border-l border-[var(--lp-line-soft)] bg-[var(--lp-accent-soft)]">
                <p className="text-sm font-bold text-[var(--lp-accent-ink)]">With Autopilot Monitor</p>
              </div>
              <div className="px-5 py-4 border-l border-[var(--lp-line-soft)]">
                <p className="text-sm font-semibold text-[var(--lp-ink-faint)]">Standard Autopilot</p>
              </div>
            </div>

            {ROWS.map(row => (
              <div
                key={row.label}
                className="grid md:grid-cols-[220px_1fr_1fr] border-b border-[var(--lp-line-soft)] last:border-b-0"
              >
                <div className="px-5 pt-4 md:py-4 text-sm font-semibold text-[var(--lp-ink)]">{row.label}</div>
                <div className="px-5 py-2 md:py-4 md:border-l border-[var(--lp-line-soft)] md:bg-[var(--lp-accent-soft)] flex items-start gap-2.5">
                  <svg className="w-4 h-4 mt-0.5 text-[var(--lp-accent-ink)] shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                  </svg>
                  <span className="text-sm text-[var(--lp-ink-soft)]">{row.monitor}</span>
                </div>
                <div className="px-5 py-2 pb-4 md:py-4 md:border-l border-[var(--lp-line-soft)] flex items-start gap-2.5">
                  <svg className="w-4 h-4 mt-0.5 text-[var(--lp-ink-faint)] shrink-0 opacity-60" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                  </svg>
                  <span className="text-sm text-[var(--lp-ink-faint)]">{row.standard}</span>
                </div>
              </div>
            ))}
          </div>
        </Reveal>
      </div>
    </section>
  );
}
