import { LoginButton } from "./LoginButton";
import { HeroCockpit } from "./HeroCockpit";
import { StatsBand } from "./StatsBand";
import { DOCS_URL } from "@/utils/config";

export function Hero() {
  return (
    <header className="relative pt-24 sm:pt-28 pb-16 px-6 overflow-hidden">
      {/* Soft accent glow behind the cockpit */}
      <div className="absolute top-40 left-1/2 -translate-x-1/2 w-[1100px] h-[520px] bg-[radial-gradient(ellipse_at_center,var(--lp-accent-soft),transparent_65%)] pointer-events-none" />

      <div className="relative max-w-6xl mx-auto">
        {/* Left-aligned, compact headline block */}
        <div className="max-w-3xl">
          <div className="inline-flex items-center gap-2 rounded-full border border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)] px-3.5 py-1.5">
            <span className="w-1.5 h-1.5 rounded-full bg-[var(--lp-accent)] lp-live-dot" />
            <span className="text-xs font-semibold text-[var(--lp-accent-ink)]">Private preview running — free &amp; open source</span>
          </div>

          <h1 className="mt-6 text-4xl sm:text-[3.4rem] font-bold tracking-tight text-[var(--lp-ink)] leading-[1.06] text-balance">
            Your Autopilot rollout is a black box.{" "}
            <span className="text-[var(--lp-accent-ink)]">Not anymore.</span>
          </h1>

          <p className="mt-5 text-lg sm:text-xl text-[var(--lp-ink-soft)] leading-relaxed max-w-2xl">
            Every enrollment live. Every failure explained — automatically. And an AI you can
            ask <em className="not-italic font-semibold text-[var(--lp-ink)]">why</em>.
          </p>

          <div className="mt-7 flex flex-wrap items-center gap-3">
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
            <span className="text-sm text-[var(--lp-ink-faint)] sm:ml-2">
              Deploy once via Intune — live minutes later.
            </span>
          </div>
        </div>

        {/* Mission control — the product, running */}
        <div className="mt-10 sm:mt-12">
          <HeroCockpit />
        </div>

        <div className="mt-12">
          <StatsBand />
        </div>
      </div>
    </header>
  );
}
