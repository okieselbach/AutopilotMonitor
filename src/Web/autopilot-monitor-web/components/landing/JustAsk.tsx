import Link from "next/link";
import { McpTerminalDemo } from "./McpTerminalDemo";

export function JustAsk() {
  return (
    <section data-track-section="just_ask" className="py-16 sm:py-20 px-6">
      <div className="max-w-7xl mx-auto grid lg:grid-cols-[1fr_1.7fr] gap-6 lg:gap-10 items-center">
        <div>
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-accent-ink)]">
            Built-in MCP server
          </p>
          <h2 className="mt-3 text-2xl sm:text-3xl font-bold tracking-tight text-[var(--lp-ink)]">
            Then just ask.
          </h2>
          <p className="mt-3 text-[15px] text-[var(--lp-ink-soft)] leading-relaxed">
            Your AI assistant reads the whole session for you — and finds the root cause a human
            would dig for all afternoon. This analysis is real.
          </p>
          <Link
            href="/ai"
            prefetch={false}
            data-track="just_ask_ai"
            className="mt-5 inline-flex items-center gap-1.5 text-[15px] font-semibold text-[var(--lp-accent-ink)] hover:opacity-80 transition-opacity"
          >
            See what else you can ask
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.25} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="M5 12h14M13 5l7 7-7 7" />
            </svg>
          </Link>
        </div>
        <div className="min-w-0">
          <McpTerminalDemo />
        </div>
      </div>
    </section>
  );
}
