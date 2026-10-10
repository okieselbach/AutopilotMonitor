import type { ReactNode } from "react";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { DOCS_URL } from "@/lib/config";
import { CopyServerUrl } from "./CopyServerUrl";
import { ArrowRightIcon } from "./icons";

function Step({ n, title, children }: { n: number; title: string; children: ReactNode }) {
  return (
    <li className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-bg)] p-5">
      <div className="flex items-start gap-4">
        <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-[var(--lp-accent)] text-sm font-bold text-white">
          {n}
        </span>
        <div className="min-w-0 flex-1">
          <h3 className="text-[15px] font-semibold text-[var(--lp-ink)]">{title}</h3>
          <div className="mt-1 text-sm leading-relaxed text-[var(--lp-ink-soft)]">{children}</div>
        </div>
      </div>
    </li>
  );
}

export function ConnectSection() {
  return (
    <section
      id="connect"
      data-track-section="connect"
      className="scroll-mt-20 border-y border-[var(--lp-line-soft)] bg-[var(--lp-surface)] px-6 py-20 sm:py-24"
    >
      <div className="mx-auto grid max-w-7xl grid-cols-1 items-start gap-10 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.2fr)] lg:gap-16">
        <div className="max-w-xl">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Connect</p>
          <h2 className="mt-3 text-balance text-3xl font-bold tracking-tight text-[var(--lp-ink)] sm:text-4xl">Connected in a minute.</h2>
          <p className="mt-4 text-lg leading-relaxed text-[var(--lp-ink-soft)]">
            There are no API keys and no local server to install. You need a role in your organization&apos;s Autopilot
            Monitor tenant.
          </p>
          <a
            href={`${DOCS_URL}${DOCS_PATHS.mcpUsers}`}
            target="_blank"
            rel="noopener noreferrer"
            data-track="setup_guide"
            className="mt-6 inline-flex items-center gap-2 text-[15px] font-semibold text-[var(--lp-accent-ink)] hover:opacity-80"
          >
            Setup guide for every client
            <ArrowRightIcon />
          </a>
        </div>
        <ol className="grid grid-cols-1 gap-4">
          <Step n={1} title="Add the server">
            Paste this URL as a custom connector or MCP server in your client.
            <CopyServerUrl />
          </Step>
          <Step n={2} title="Sign in with Microsoft">
            Your browser opens the Microsoft sign-in. Use the work account you use for Autopilot Monitor.
          </Step>
          <Step n={3} title="Ask your first question">
            {"For example: "}
            <span className="font-medium text-[var(--lp-ink)]">{"“Which enrollments failed this week, and why?”"}</span>
          </Step>
        </ol>
      </div>
    </section>
  );
}
