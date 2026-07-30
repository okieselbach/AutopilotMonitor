/**
 * Act 2 visual — the analysis engine flags ANALYZE-ESP-001 (ESP Blocking
 * App Timeout), one of the platform's most frequently firing rules.
 */
export function StoryAnalysis() {
  return (
    <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-xl shadow-black/[0.06] overflow-hidden">
      <div className="flex items-center justify-between px-4 py-2.5 border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]">
        <span className="text-xs font-semibold text-[var(--lp-ink)]">Analysis — CONTOSO-4711</span>
        <span className="text-[10px] font-semibold px-2 py-0.5 rounded-full text-[var(--lp-warn)] bg-[var(--lp-warn-soft)]">
          1 issue detected
        </span>
      </div>

      <div className="p-4 sm:p-5">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide rounded bg-[#ff7a45] text-white">High</span>
          <span className="font-mono text-[11px] text-[var(--lp-ink-faint)]">ANALYZE-ESP-001</span>
          <span className="ml-auto hidden sm:flex items-center gap-1.5 text-[10px] text-[var(--lp-ink-faint)]">
            <svg className="w-3 h-3 text-[var(--lp-accent)]" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-1a4 4 0 00-5-3.87M9 20H2v-1a4 4 0 015-3.87m9-5.13a4 4 0 11-8 0 4 4 0 018 0z" />
            </svg>
            Community rule
          </span>
        </div>

        <h4 className="mt-2.5 text-base font-bold text-[var(--lp-ink)]">ESP Blocking App Timeout</h4>
        <div className="mt-1.5 flex items-center gap-2">
          <span className="text-[11px] text-[var(--lp-ink-faint)]">Confidence:</span>
          <div className="w-24 h-1.5 rounded-full bg-[var(--lp-line)] overflow-hidden">
            <div className="h-full w-[80%] rounded-full bg-[#ff7a45]" />
          </div>
          <span className="text-[11px] font-semibold text-[var(--lp-ink-soft)]">80%</span>
        </div>
        <p className="mt-2 text-sm text-[var(--lp-ink-soft)] leading-relaxed">
          Device Setup has been running for <span className="font-semibold text-[var(--lp-ink)]">39 minutes</span> — a
          blocking app is stuck, downloading very slowly, or in a retry loop.
        </p>

        {/* The culprit */}
        <div className="mt-4 rounded-xl border border-[var(--lp-line)] bg-[var(--lp-surface-2)] p-3.5">
          <div className="flex items-center justify-between gap-3">
            <span className="text-[13px] font-semibold text-[var(--lp-ink)] truncate">Contoso VPN Client</span>
            <span className="text-[11px] font-mono text-[var(--lp-warn)] shrink-0">stalled · 23 min</span>
          </div>
          <div className="mt-2 h-1.5 rounded-full bg-[var(--lp-line)] overflow-hidden">
            <div className="h-full w-[87%] rounded-full bg-[var(--lp-warn)]" />
          </div>
          <div className="mt-1.5 flex items-center justify-between text-[10px] text-[var(--lp-ink-faint)]">
            <span>download_progress · 87%</span>
            <span>no progress since 09:18</span>
          </div>
        </div>

        {/* Remediation */}
        <div className="mt-4">
          <p className="text-[11px] font-semibold uppercase tracking-[0.14em] text-[var(--lp-ink-faint)] mb-2">Suggested remediation</p>
          <ul className="space-y-1.5">
            {[
              "Identify the blocking app (done — see above)",
              "Verify the app content is downloadable",
              "Consider reducing the number of blocking apps",
            ].map(step => (
              <li key={step} className="flex items-start gap-2 text-[13px] text-[var(--lp-ink-soft)]">
                <svg className="w-3.5 h-3.5 mt-0.5 text-[var(--lp-accent)] shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                </svg>
                {step}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}
