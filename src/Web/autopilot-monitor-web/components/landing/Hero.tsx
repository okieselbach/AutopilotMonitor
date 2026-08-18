import { HeroShots } from "./HeroShots";
import { DOCS_URL } from "@/utils/config";

const SCENARIOS = [
  "User-driven",
  "Pre-provisioning",
  "Self-deploying",
  "Device Preparation",
  "Windows 365",
  "Entra ID & Hybrid join",
];

export function Hero() {
  return (
    <header className="pt-16 sm:pt-20 pb-0 px-6">
      <div className="max-w-7xl mx-auto">
        <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">
          Windows Autopilot monitoring
        </p>

        <h1 className="mt-5 text-5xl sm:text-6xl lg:text-7xl font-bold tracking-tight text-[var(--lp-ink)] leading-[1.04] max-w-4xl text-balance">
          See every Autopilot enrollment. Live.
        </h1>

        {/* Sub left, CTAs right */}
        <div className="mt-8 flex flex-col lg:flex-row lg:items-end lg:justify-between gap-6">
          <p className="text-lg sm:text-xl text-[var(--lp-ink-soft)] leading-relaxed max-w-xl">
            Real-time monitoring, automated root-cause analysis, and on-demand diagnostics for
            Windows Autopilot — deployed once via Intune, live minutes later.
          </p>
          <div className="shrink-0">
            <div className="flex items-center gap-3">
              <a
                href="/get-started"
                className="px-6 py-3 rounded-lg bg-[var(--lp-accent-ink)] hover:brightness-110 hover:shadow-lg text-white font-semibold shadow-md transition-all"
              >
                Get started
              </a>
              <a
                href={DOCS_URL}
                target="_blank"
                rel="noopener noreferrer"
                className="px-6 py-3 rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] text-[var(--lp-ink)] font-semibold hover:border-[var(--lp-ink-faint)] transition-colors"
              >
                View docs
              </a>
            </div>
            <p className="mt-3 text-sm text-[var(--lp-ink-faint)] lg:text-right">
              Free &amp; open source · no infrastructure on your side
            </p>
          </div>
        </div>

        {/* Scenario coverage — quiet chips, no competition with the CTAs */}
        <div className="mt-7 flex flex-wrap items-center gap-x-4 gap-y-2">
          {SCENARIOS.map((scenario) => (
            <span
              key={scenario}
              className="inline-flex items-center gap-1.5 text-[13px] text-[var(--lp-ink-soft)]"
            >
              <svg
                className="w-3.5 h-3.5 text-[var(--lp-accent-ink)] shrink-0"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                strokeWidth={3}
              >
                <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
              </svg>
              {scenario}
            </span>
          ))}
        </div>

        {/* The portal, front and center */}
        <div className="mt-12 sm:mt-14 pb-16 sm:pb-20">
          <HeroShots />
        </div>
      </div>
    </header>
  );
}
