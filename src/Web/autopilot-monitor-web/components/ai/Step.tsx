import type { ReactNode } from "react";

/** A numbered step card of the /ai page (Connect, From finding to rule). */
export function Step({ n, title, children }: { n: number; title: string; children: ReactNode }) {
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
