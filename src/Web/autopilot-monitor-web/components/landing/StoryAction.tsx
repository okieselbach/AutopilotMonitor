/**
 * Act 3 visual — the admin's move: the alert already reached the team,
 * and the diagnostics package is one click away. Both flows are real
 * product features (Teams/webhook notifications, on-demand diagnostics).
 */
export function StoryAction() {
  return (
    <div className="space-y-4">
      {/* Notification card */}
      <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-xl shadow-black/[0.06] overflow-hidden">
        <div className="flex items-center gap-2 px-4 py-2.5 border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]">
          <svg className="w-3.5 h-3.5 text-[var(--lp-ink-faint)]" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M15 17h5l-1.4-1.4A2 2 0 0118 14.2V11a6 6 0 10-12 0v3.2c0 .5-.2 1-.6 1.4L4 17h5m6 0a3 3 0 11-6 0h6z" />
          </svg>
          <span className="text-xs font-semibold text-[var(--lp-ink)]">Teams · #intune-ops</span>
          <span className="ml-auto text-[10px] text-[var(--lp-ink-faint)]">09:41</span>
        </div>
        <div className="p-4">
          <div className="flex items-center gap-2 flex-wrap">
            <span className="px-1.5 py-0.5 text-[9px] font-bold uppercase rounded bg-[#ff7a45] text-white">High</span>
            <span className="text-[13px] font-semibold text-[var(--lp-ink)]">ESP Blocking App Timeout</span>
          </div>
          <p className="mt-1.5 text-[12.5px] text-[var(--lp-ink-soft)] leading-relaxed">
            <span className="font-semibold text-[var(--lp-ink)]">CONTOSO-4711</span> — Device Setup
            running 39 min, blocking app <span className="font-medium">Contoso VPN Client</span> stalled at 87%.
          </p>
          {/* The real Teams card carries exactly one action: "Open session" */}
          <div className="mt-3 flex items-center gap-2">
            <span className="px-3 py-1.5 rounded-lg bg-[var(--lp-accent-ink)] text-white text-[11px] font-semibold">Open session</span>
          </div>
        </div>
      </div>

      {/* Diagnostics flow */}
      <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-xl shadow-black/[0.06] overflow-hidden">
        <div className="flex items-center justify-between px-4 py-2.5 border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]">
          <span className="text-xs font-semibold text-[var(--lp-ink)]">Diagnostics — CONTOSO-4711</span>
          <span className="text-[10px] font-semibold px-2 py-0.5 rounded-full text-[var(--lp-accent-ink)] bg-[var(--lp-accent-soft)]">ZIP ready</span>
        </div>
        <div className="p-4">
          <ul className="space-y-2">
            {[
              { label: "Requested from the portal", time: "09:42:10" },
              { label: "Collected on the device — mid-enrollment", time: "09:43:05" },
              { label: "Uploaded · 14.2 MB", time: "09:43:41" },
            ].map(step => (
              <li key={step.label} className="flex items-center gap-2.5 text-[12.5px]">
                <span className="w-4 h-4 rounded-full bg-[var(--lp-accent)] flex items-center justify-center shrink-0">
                  <svg className="w-2.5 h-2.5 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3.5}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                  </svg>
                </span>
                <span className="text-[var(--lp-ink-soft)]">{step.label}</span>
                <span className="ml-auto font-mono text-[10px] text-[var(--lp-ink-faint)] shrink-0">{step.time}</span>
              </li>
            ))}
          </ul>
          <div className="mt-3.5 flex items-center justify-between rounded-xl border border-[var(--lp-line)] bg-[var(--lp-surface-2)] px-3.5 py-2.5">
            <span className="flex items-center gap-2 text-[12.5px] font-medium text-[var(--lp-ink)] min-w-0">
              <svg className="w-4 h-4 text-[var(--lp-accent-ink)] shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M20 13V7a2 2 0 00-2-2h-5.6a1 1 0 01-.7-.3l-1.4-1.4a1 1 0 00-.7-.3H6a2 2 0 00-2 2v12a2 2 0 002 2h12a2 2 0 002-2v-2m-8-6v6m0 0l-2.5-2.5M12 17l2.5-2.5" />
              </svg>
              <span className="truncate">CONTOSO-4711_diagnostics.zip</span>
            </span>
            <span className="text-[11px] font-semibold text-[var(--lp-accent-ink)] shrink-0">Download</span>
          </div>
        </div>
      </div>
    </div>
  );
}
