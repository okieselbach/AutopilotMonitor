import { McpTerminalDemo } from "./McpTerminalDemo";

export function JustAsk() {
  return (
    <section className="py-16 sm:py-20 px-6">
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
        </div>
        <div className="min-w-0">
          <McpTerminalDemo />
        </div>
      </div>
    </section>
  );
}
