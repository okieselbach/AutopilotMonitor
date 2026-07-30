const WEEK_BARS = [
  { day: "Mon", ok: 34, fail: 2 },
  { day: "Tue", ok: 41, fail: 1 },
  { day: "Wed", ok: 28, fail: 4 },
  { day: "Thu", ok: 46, fail: 2 },
  { day: "Fri", ok: 38, fail: 1 },
  { day: "Sat", ok: 9, fail: 0 },
  { day: "Sun", ok: 6, fail: 1 },
];

const HOTSPOTS = [
  { label: "Contoso VPN Client", detail: "in 62% of slow enrollments", warn: true },
  { label: "ThinkPad X1 Carbon G12", detail: "avg 21 min · 97% success", warn: false },
  { label: "Surface Laptop 7", detail: "avg 18 min · 99% success", warn: false },
];

const MAX_BAR = 48;

/**
 * Act 4 visual — the zoom-out from one session to fleet health.
 */
export function StoryFleet() {
  return (
    <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-xl shadow-black/[0.06] overflow-hidden">
      <div className="flex items-center justify-between px-4 py-2.5 border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]">
        <span className="text-xs font-semibold text-[var(--lp-ink)]">Fleet Health — last 7 days</span>
        <span className="flex items-center gap-1.5 text-[10px] text-[var(--lp-ink-faint)]">
          <span className="w-1.5 h-1.5 rounded-full bg-[var(--lp-accent)] lp-live-dot" />
          3 enrolling now
        </span>
      </div>

      <div className="p-4 sm:p-5">
        {/* KPI row */}
        <div className="grid grid-cols-3 gap-3">
          {[
            { label: "Success rate", value: "94.6%", accent: true },
            { label: "Avg. duration", value: "22 min", accent: false },
            { label: "Enrollments", value: "213", accent: false },
          ].map(kpi => (
            <div key={kpi.label} className="rounded-xl border border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)] px-3 py-2.5">
              <p className={`text-lg font-bold tracking-tight ${kpi.accent ? "text-[var(--lp-accent-ink)]" : "text-[var(--lp-ink)]"}`}>{kpi.value}</p>
              <p className="text-[10px] text-[var(--lp-ink-faint)]">{kpi.label}</p>
            </div>
          ))}
        </div>

        {/* Weekly bars */}
        <div className="mt-4 flex items-end gap-2 h-20 px-1">
          {WEEK_BARS.map(bar => {
            const total = bar.ok + bar.fail;
            const height = Math.max(6, (total / MAX_BAR) * 72);
            const failHeight = bar.fail > 0 ? Math.max(3, (bar.fail / total) * height) : 0;
            return (
              <div key={bar.day} className="flex-1 flex flex-col items-center gap-1">
                <div className="w-full flex flex-col justify-end rounded overflow-hidden" style={{ height: `${height}px` }}>
                  <div className="w-full bg-[var(--lp-accent)] opacity-80" style={{ height: `${height - failHeight}px` }} />
                  {failHeight > 0 && <div className="w-full bg-[var(--lp-danger)] opacity-70" style={{ height: `${failHeight}px` }} />}
                </div>
                <span className="text-[9px] text-[var(--lp-ink-faint)]">{bar.day}</span>
              </div>
            );
          })}
        </div>

        {/* Hotspots */}
        <div className="mt-4">
          <p className="text-[11px] font-semibold uppercase tracking-[0.14em] text-[var(--lp-ink-faint)] mb-2">Hotspots across the fleet</p>
          <ul className="space-y-1.5">
            {HOTSPOTS.map(item => (
              <li key={item.label} className="flex items-center justify-between gap-3 text-[13px]">
                <span className="flex items-center gap-2 min-w-0">
                  <span className={`w-1.5 h-1.5 rounded-full shrink-0 ${item.warn ? "bg-[var(--lp-warn)]" : "bg-[var(--lp-accent)]"}`} />
                  <span className="font-medium text-[var(--lp-ink)] truncate">{item.label}</span>
                </span>
                <span className="text-[11px] text-[var(--lp-ink-faint)] shrink-0">{item.detail}</span>
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}
