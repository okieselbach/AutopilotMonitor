import type { ReactNode } from "react";
import { communityFeatures, PRO_PRICE, proExtras, sharedFeatures } from "./planData";

/**
 * Shared Community/Pro comparison cards — the ONE place the two plans are laid
 * out. The portal Plan section and the public /plans page both render this;
 * plan copy itself lives in planData.ts.
 *
 * Surfaces:
 * - "portal": plain Tailwind palette + explicit `dark:` variants. The tinted
 *   card surfaces use slash-opacity utilities (bg-purple-50/40), which the
 *   global `.dark .bg-*` override map in globals.css does NOT match — they are
 *   separate class tokens. Without the explicit `dark:` counterparts the cards
 *   render as a washed-out light overlay on the dark page. Secondary copy uses
 *   text-gray-600 rather than -400/-500: both of those collapse to the same dim
 *   #64748b in dark mode, which fails contrast on a tinted card.
 * - "marketing": renders inside the light-only `.landing-v2` shell, so no
 *   `dark:` variants (they key on `.dark` at <html> and would bleed through).
 *   The purple utilities used here are neutralized under `.dark .landing-v2`
 *   in globals.css, like the gray/white ones.
 */

function CheckIcon({ className }: { className: string }) {
  return (
    <svg className={className} fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={2.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
    </svg>
  );
}

function PlusIcon({ className }: { className: string }) {
  return (
    <svg className={className} fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={2.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M12 5v14M5 12h14" />
    </svg>
  );
}

function FeatureList({ features, checkClass, muted = false }: { features: string[]; checkClass: string; muted?: boolean }) {
  return (
    <ul className="space-y-2.5">
      {features.map((f) => (
        <li key={f} className={`flex items-start gap-2 text-sm ${muted ? "text-gray-600" : "text-gray-700"}`}>
          <CheckIcon className={`w-4 h-4 mt-0.5 shrink-0 ${checkClass}`} />
          <span>{f}</span>
        </li>
      ))}
    </ul>
  );
}

/** The Pro delta: same layout as FeatureList but "+" bullets and emphasized text. */
function PlusList({ features, iconClass }: { features: string[]; iconClass: string }) {
  return (
    <ul className="space-y-2.5">
      {features.map((f) => (
        <li key={f} className="flex items-start gap-2 text-sm font-medium text-gray-900">
          <PlusIcon className={`w-4 h-4 mt-0.5 shrink-0 ${iconClass}`} />
          <span>{f}</span>
        </li>
      ))}
    </ul>
  );
}

export interface PlanCardsProps {
  surface: "marketing" | "portal";
  /** Which card gets the "this is yours" highlight treatment (portal only). */
  highlight?: "community" | "pro" | null;
  communityBadge?: ReactNode;
  proBadge?: ReactNode;
  /** Overrides the Pro price line (portal shows "Active" while on Pro). */
  proPrice?: ReactNode;
  /** Rendered at the bottom of the Pro card — trial CTA on portal, buy link on marketing. */
  proCta?: ReactNode;
}

export function PlanCards({ surface, highlight = null, communityBadge, proBadge, proPrice, proCta }: PlanCardsProps) {
  const marketing = surface === "marketing";

  const communityCardClass =
    highlight === "community"
      ? "border-gray-800 ring-1 ring-gray-800 bg-gray-50/60 dark:border-slate-500 dark:ring-slate-500 dark:bg-slate-900/40"
      : marketing
        ? "border-[var(--lp-line-soft)] bg-[var(--lp-surface)]"
        : "border-gray-200";

  const proCardClass =
    highlight === "pro"
      ? "border-purple-500 ring-1 ring-purple-500 bg-purple-50/40 dark:bg-purple-950/40"
      : marketing
        ? "border-purple-200 bg-purple-50/20"
        : "border-purple-200 bg-purple-50/20 dark:bg-purple-950/20";

  const plusIconClass = marketing ? "text-purple-600" : "text-purple-600 dark:text-purple-400";
  const dividerClass = marketing ? "bg-purple-200" : "bg-purple-200 dark:bg-purple-800";
  const dividerLabelClass = marketing ? "text-purple-600" : "text-purple-600 dark:text-purple-300";

  return (
    <div className="grid grid-cols-1 lg:grid-cols-2 gap-5">
      {/* Community card */}
      <div className={`rounded-xl border p-6 flex flex-col ${communityCardClass}`}>
        <div className="flex items-start justify-between gap-2">
          <div>
            <h3 className="text-lg font-semibold text-gray-900">Community</h3>
            <p className="text-sm text-gray-600 mt-0.5">The full product, free</p>
          </div>
          {communityBadge}
        </div>

        <div className="mt-4 mb-5">
          <span className="text-2xl font-bold text-gray-900">Free</span>
          <span className="text-sm text-gray-600"> — and stays free</span>
        </div>

        <FeatureList features={communityFeatures} checkClass="text-emerald-500" />
      </div>

      {/* Pro card */}
      <div className={`rounded-xl border p-6 flex flex-col ${proCardClass}`}>
        <div className="flex items-start justify-between gap-2">
          <div>
            <h3 className="text-lg font-semibold text-purple-900">Pro</h3>
            <p className="text-sm text-gray-600 mt-0.5">Higher limits, support &amp; MSP</p>
          </div>
          {proBadge}
        </div>

        <div className="mt-4 mb-5">
          {proPrice ?? (
            PRO_PRICE ? (
              <>
                <span className="text-2xl font-bold text-purple-900">{PRO_PRICE.amount}</span>
                <span className="text-sm text-gray-600"> {PRO_PRICE.suffix}</span>
              </>
            ) : (
              <>
                <span className="text-2xl font-bold text-purple-900">Pricing</span>
                <span className="text-sm text-gray-600"> — announced soon</span>
              </>
            )
          )}
        </div>

        <p className="text-xs font-medium uppercase tracking-wide text-gray-600 mb-2.5">
          Everything in Community
        </p>
        <FeatureList features={sharedFeatures} checkClass="text-gray-500" muted />

        <div className="flex items-center gap-3 my-4" aria-hidden="true">
          <span className={`h-px flex-1 ${dividerClass}`} />
          <span className={`text-xs font-semibold uppercase tracking-wide ${dividerLabelClass}`}>Plus</span>
          <span className={`h-px flex-1 ${dividerClass}`} />
        </div>
        <PlusList features={proExtras} iconClass={plusIconClass} />

        {proCta && <div className="mt-auto pt-5">{proCta}</div>}
      </div>
    </div>
  );
}
