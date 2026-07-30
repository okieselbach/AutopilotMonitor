/**
 * Hero visual: real product screenshots, layered. The dashboard is the
 * base; a real analysis finding (with remediation + evidence) overlaps
 * bottom-right — fleet visibility and root-cause power in one glance.
 * Captured from the live portal with staged demo data (no customer data).
 */
export function HeroShots() {
  return (
    <div className="relative">
      <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-2xl shadow-black/[0.12] overflow-hidden">
        {/* Static export: next/image is not configured, plain img is intentional */}
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img
          src="/landing/portal-dashboard.png"
          alt="Autopilot Monitor dashboard with live enrollment sessions"
          width={1600}
          height={950}
          className="w-full h-auto block"
        />
      </div>

      <div className="hidden sm:block absolute -bottom-14 right-[-8px] lg:right-[-28px] w-[52%] max-w-[600px] rounded-xl border border-[var(--lp-line)] shadow-2xl shadow-black/[0.18] overflow-hidden bg-white">
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img
          src="/landing/analysis-finding.png"
          alt="Automated analysis finding with severity, remediation steps, and event evidence"
          width={1058}
          height={612}
          className="w-full h-auto block"
        />
      </div>

      {/* Spacer for the overlapping card */}
      <div className="hidden sm:block h-16" />
    </div>
  );
}
