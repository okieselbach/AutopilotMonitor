import { LoginButton } from "./LoginButton";
import { HeroShots } from "./HeroShots";
import { McpTerminalDemo } from "./McpTerminalDemo";
import { StatsBand } from "./StatsBand";
import { DOCS_URL } from "@/utils/config";

export function Hero() {
  return (
    <header className="relative pt-24 sm:pt-28 pb-16 px-6 overflow-hidden">
      {/* Soft accent glow behind the product shot */}
      <div className="absolute top-40 left-1/2 -translate-x-1/2 w-[1100px] h-[520px] bg-[radial-gradient(ellipse_at_center,var(--lp-accent-soft),transparent_65%)] pointer-events-none" />

      <div className="relative max-w-6xl mx-auto">
        {/* Compact, refined headline block */}
        <div className="max-w-3xl">
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-accent-ink)]">
            Windows Autopilot monitoring
          </p>
          <h1 className="mt-4 text-4xl sm:text-5xl font-bold tracking-tight text-[var(--lp-ink)] leading-[1.08] text-balance">
            Your Autopilot rollout is a black box.{" "}
            <span className="text-[var(--lp-accent-ink)]">Not anymore.</span>
          </h1>
          <p className="mt-4 text-lg text-[var(--lp-ink-soft)] leading-relaxed max-w-xl">
            Every enrollment live. Every failure explained — automatically. And an AI you can
            ask <em className="not-italic font-semibold text-[var(--lp-ink)]">why</em>.
          </p>
          <div className="mt-6 flex flex-wrap items-center gap-3">
            <LoginButton className="px-6 py-2.5 rounded-lg bg-[var(--lp-accent)] hover:brightness-105 text-white font-semibold shadow-md shadow-[var(--lp-accent-soft)] transition-all hover:-translate-y-0.5">
              Get Started
            </LoginButton>
            <a
              href={DOCS_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="px-6 py-2.5 rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] text-[var(--lp-ink)] font-semibold hover:border-[var(--lp-ink-faint)] transition-colors"
            >
              View Docs
            </a>
            <span className="text-sm text-[var(--lp-ink-faint)]">
              Free &amp; open source · deploy once via Intune
            </span>
          </div>
        </div>

        {/* The real product */}
        <div className="mt-10 sm:mt-12">
          <HeroShots />
        </div>

        {/* …and the AI on top of it */}
        <div className="mt-10 sm:mt-14 grid lg:grid-cols-[1fr_1.7fr] gap-6 lg:gap-10 items-center">
          <div>
            <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-accent-ink)]">
              Built-in MCP server
            </p>
            <h2 className="mt-3 text-2xl sm:text-3xl font-bold tracking-tight text-[var(--lp-ink)]">
              Then just ask.
            </h2>
            <p className="mt-3 text-[15px] text-[var(--lp-ink-soft)] leading-relaxed">
              Your AI assistant reads the whole session for you — and finds the root cause a
              human would dig for all afternoon. This analysis is real.
            </p>
          </div>
          <div className="min-w-0">
            <McpTerminalDemo />
          </div>
        </div>

        <div className="mt-14">
          <StatsBand />
        </div>
      </div>
    </header>
  );
}
