/**
 * Hero visual: the real product. Captured from the live portal with
 * staged demo data (no customer data, preview banner removed).
 */
export function HeroShots() {
  return (
    <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-2xl shadow-black/[0.12] overflow-hidden">
      {/* Static export: next/image is not configured, plain img is intentional.
          This is the LCP element: keep it a plain <img> (no <picture>, no
          loading="lazy") so React emits its <link rel="preload" as="image"> with
          imagesrcset/imagesizes. WebP only — every supported browser decodes it;
          the three widths are cut from one 1600x950 screenshot. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src="/landing/portal-dashboard-1600.webp"
        srcSet="/landing/portal-dashboard-800.webp 800w, /landing/portal-dashboard-1200.webp 1200w, /landing/portal-dashboard-1600.webp 1600w"
        sizes="(min-width: 1280px) 1232px, calc(100vw - 48px)"
        alt="Autopilot Monitor dashboard with live enrollment sessions"
        width={1600}
        height={950}
        decoding="async"
        className="w-full h-auto block"
      />
    </div>
  );
}
