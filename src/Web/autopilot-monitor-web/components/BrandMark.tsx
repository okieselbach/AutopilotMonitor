/**
 * The Autopilot Monitor brand mark — the green sail from app/icon.svg,
 * kept here as the single inline-SVG source for navbars, footer, and hero.
 * The favicon (app/icon.svg) must stay in sync with this path.
 */
export function BrandMark({ className = "w-6 h-6" }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 209 191" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
      <path d="M0 180.201L208.401 190.502L188.157 76.2438L5.48363e-06 0L0 180.201Z" fill="#33B161" />
    </svg>
  );
}
