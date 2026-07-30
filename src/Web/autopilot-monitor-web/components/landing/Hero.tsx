import { LoginButton } from "./LoginButton";
import { LiveTimeline } from "./LiveTimeline";
import { StatsBand } from "./StatsBand";
import { DOCS_URL } from "@/utils/config";

export function Hero() {
  return (
    <header className="relative pt-28 sm:pt-32 pb-16 px-6 overflow-hidden">
      {/* Soft accent glow behind the headline */}
      <div className="absolute top-0 left-1/2 -translate-x-1/2 w-[900px] h-[420px] bg-[radial-gradient(ellipse_at_center,var(--lp-accent-soft),transparent_65%)] pointer-events-none" />

      <div className="relative max-w-6xl mx-auto">
        <div className="max-w-3xl mx-auto text-center">
          <div className="inline-flex items-center gap-2 rounded-full border border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)] px-3.5 py-1.5 mb-7">
            <span className="w-1.5 h-1.5 rounded-full bg-[var(--lp-accent)] lp-live-dot" />
            <span className="text-xs font-semibold text-[var(--lp-accent-ink)]">Private preview running — free &amp; open source</span>
          </div>

          <h1 className="text-4xl sm:text-6xl font-bold tracking-tight text-[var(--lp-ink)] leading-[1.05] text-balance">
            Your Autopilot rollout is a black box.
            <span className="block mt-2 text-[var(--lp-accent-ink)]">Not anymore.</span>
          </h1>

          <p className="mt-6 text-lg sm:text-xl text-[var(--lp-ink-soft)] leading-relaxed max-w-2xl mx-auto">
            Autopilot Monitor streams every Windows enrollment live — every phase, every app
            install, every error — and tells you <em className="not-italic font-semibold text-[var(--lp-ink)]">why</em> things
            break before your users call.
          </p>

          <div className="mt-8 flex flex-wrap items-center justify-center gap-3">
            <LoginButton className="px-7 py-3 rounded-xl bg-[var(--lp-accent)] hover:brightness-105 text-white font-semibold shadow-lg shadow-[var(--lp-accent-soft)] transition-all hover:-translate-y-0.5">
              Get Started
            </LoginButton>
            <a
              href={DOCS_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="px-7 py-3 rounded-xl border border-[var(--lp-line)] bg-[var(--lp-surface)] text-[var(--lp-ink)] font-semibold hover:border-[var(--lp-ink-faint)] transition-colors"
            >
              View Docs
            </a>
          </div>
          <p className="mt-4 text-sm text-[var(--lp-ink-faint)]">
            Deploy once via Intune — watch your first enrollment minutes later.
          </p>
        </div>

        {/* The product, live — not a screenshot */}
        <div className="mt-14 max-w-3xl mx-auto">
          <LiveTimeline />
          <p className="mt-3 text-center text-xs text-[var(--lp-ink-faint)]">
            What a live session looks like — streamed from the device as it enrolls.
          </p>
        </div>

        <div className="mt-14">
          <StatsBand />
        </div>
      </div>
    </header>
  );
}
