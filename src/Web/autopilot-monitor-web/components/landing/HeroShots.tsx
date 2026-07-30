/**
 * Hero visual: the real product. Captured from the live portal with
 * staged demo data (no customer data, preview banner removed).
 */
export function HeroShots() {
  return (
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
  );
}
