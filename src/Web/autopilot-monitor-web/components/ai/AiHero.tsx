import { AskBar } from "./AskBar";

/** Clients documented in the customer docs (integrations/ai-integration-mcp.md, "Supported AI clients"). */
const CLIENTS = ["Claude", "ChatGPT", "VS Code & GitHub Copilot", "Claude Code", "Codex", "Gemini CLI", "Self-hosted clients"];

export function AiHero() {
  return (
    <section data-track-section="hero" className="px-6 pb-16 pt-16 sm:pb-20 sm:pt-24">
      <div className="mx-auto max-w-4xl text-center">
        <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">AI analysis with MCP</p>
        <h1 className="mt-5 text-balance text-[2.6rem] font-bold leading-[1.06] tracking-tight text-[var(--lp-ink)] sm:text-6xl sm:leading-[1.04] lg:text-7xl">
          Ask your enrollments anything.
        </h1>
        <p className="mx-auto mt-6 max-w-2xl text-lg leading-relaxed text-[var(--lp-ink-soft)] sm:text-xl">
          Autopilot Monitor records every enrollment as structured data. Connect the AI assistant you already use and ask
          in plain language, about one device or the whole fleet.
        </p>
      </div>
      <div className="mx-auto mt-10 max-w-3xl">
        <AskBar />
      </div>
      <div className="mx-auto mt-5 flex max-w-4xl flex-wrap items-center justify-center gap-2">
        <span className="mr-1 text-[13px] text-[var(--lp-ink-faint)]">Works in</span>
        {CLIENTS.map(client => (
          <span
            key={client}
            className="inline-flex items-center rounded-full border border-[var(--lp-line)] bg-[var(--lp-surface)] px-2.5 py-1 text-[13px] font-medium text-[var(--lp-ink-soft)]"
          >
            {client}
          </span>
        ))}
      </div>
      <div className="mt-10 flex flex-wrap justify-center gap-3">
        <a
          href="#connect"
          data-track="hero_connect"
          className="rounded-lg bg-[var(--lp-accent-ink)] px-6 py-3 font-semibold text-white shadow-md transition-all hover:shadow-lg hover:brightness-110"
        >
          Connect your assistant
        </a>
        <a
          href="#questions"
          data-track="hero_examples"
          className="rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] px-6 py-3 font-semibold text-[var(--lp-ink)] transition-colors hover:border-[var(--lp-ink-faint)]"
        >
          See example questions
        </a>
      </div>
      <p className="mt-3 text-center text-sm text-[var(--lp-ink-faint)]">Included in every plan · read-only · Microsoft sign-in</p>
    </section>
  );
}
