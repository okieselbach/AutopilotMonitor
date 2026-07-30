import { Reveal } from "./Reveal";

const CAPABILITIES = [
  {
    title: "Real-time event stream",
    detail: "Live phase tracking with push updates, from first boot to desktop.",
  },
  {
    title: "Analyze rules",
    detail: "Built-in, community-driven, and fully customizable issue detection.",
  },
  {
    title: "Gather rules",
    detail: "Capture registry keys, files, or WMI data on any event you choose.",
  },
  {
    title: "On-demand diagnostics",
    detail: "Pull a diagnostics package from any enrolled device — no ticket ping-pong.",
  },
  {
    title: "Event timeline",
    detail: "Per-session drill-down with app installs, errors, and IME insights.",
  },
  {
    title: "Fleet health & trends",
    detail: "Success rates, duration trends, model and app hotspots at a glance.",
  },
  {
    title: "Notifications",
    detail: "Teams and webhook alerts the moment an enrollment fails or stalls.",
  },
  {
    title: "MCP server for AI",
    detail: "Query sessions, metrics, and docs from your AI assistant.",
  },
  {
    title: "Audit log & open source",
    detail: "Full audit trail — and the whole platform is open on GitHub.",
  },
];

export function CapabilitiesStrip() {
  return (
    <section id="features" className="py-20 sm:py-24 px-6 scroll-mt-20 border-y border-[var(--lp-line-soft)] bg-[var(--lp-surface)]">
      <div className="max-w-7xl mx-auto">
        <Reveal className="max-w-2xl">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Capabilities</p>
          <h2 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)]">
            Everything the story just showed you — and more.
          </h2>
        </Reveal>

        <div className="mt-10 grid sm:grid-cols-2 lg:grid-cols-3 gap-x-10 gap-y-7">
          {CAPABILITIES.map((cap, i) => (
            <Reveal key={cap.title} delayMs={(i % 3) * 80}>
              <div className="flex items-start gap-3">
                <span className="mt-0.5 w-5 h-5 rounded-md bg-[var(--lp-accent-soft)] flex items-center justify-center shrink-0">
                  <svg className="w-3 h-3 text-[var(--lp-accent-ink)]" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                  </svg>
                </span>
                <div>
                  <h3 className="text-[15px] font-semibold text-[var(--lp-ink)]">{cap.title}</h3>
                  <p className="mt-1 text-sm text-[var(--lp-ink-soft)] leading-relaxed">{cap.detail}</p>
                </div>
              </div>
            </Reveal>
          ))}
        </div>
      </div>
    </section>
  );
}
