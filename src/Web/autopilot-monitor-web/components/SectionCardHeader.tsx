import type { ReactNode } from "react";
import { DocsLink } from "./DocsLink";

/**
 * Colour tones for section card headers. Every class string is literal so Tailwind can see it —
 * never assemble these from fragments. Tenant tones use the pastel 50-level gradient with grey
 * text; admin tones use the 100-level gradient, tinted text, and dark-mode variants.
 */
const TONES = {
  amber: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-amber-50 to-orange-50",
    icon: "w-6 h-6 text-amber-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  violet: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-violet-50 to-purple-50",
    icon: "w-6 h-6 text-violet-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  emerald: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-emerald-50 to-teal-50",
    icon: "w-6 h-6 text-emerald-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  rose: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-rose-50 to-red-50",
    icon: "w-6 h-6 text-rose-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  indigo: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-blue-50",
    icon: "w-6 h-6 text-indigo-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  indigoPurple: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-purple-50",
    icon: "w-6 h-6 text-indigo-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  sky: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-sky-50 to-blue-50",
    icon: "w-6 h-6 text-sky-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  skyIndigo: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-sky-50 to-indigo-50",
    icon: "w-6 h-6 text-sky-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  purple: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-purple-50 to-indigo-50",
    icon: "w-6 h-6 text-purple-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  green: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-green-50 to-emerald-50",
    icon: "w-6 h-6 text-green-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  cyan: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-cyan-50 to-teal-50",
    icon: "w-6 h-6 text-cyan-600",
    title: "text-xl font-semibold text-gray-900",
    subtitle: "text-sm text-gray-500 mt-1",
  },
  /** Amber gradient with amber text — guardrail-relaxing settings (Unrestricted Mode). */
  warning: {
    wrapper: "p-6 border-b border-gray-200 bg-gradient-to-r from-amber-50 to-orange-50",
    icon: "w-6 h-6 text-amber-600",
    title: "text-xl font-semibold text-amber-900",
    subtitle: "text-sm text-amber-700 mt-1",
  },
  /** Flat red — irreversible actions (Danger Zone). */
  danger: {
    wrapper: "p-6 border-b border-red-100 bg-red-50",
    icon: "w-6 h-6 text-red-600",
    title: "text-xl font-semibold text-red-900",
    subtitle: "text-sm text-red-600 mt-1",
  },
  adminIndigo: {
    wrapper: "p-6 border-b border-indigo-200 dark:border-indigo-700 bg-gradient-to-r from-indigo-100 to-blue-100 dark:from-indigo-900/40 dark:to-blue-900/40",
    icon: "w-6 h-6 text-indigo-600 dark:text-indigo-400",
    title: "text-xl font-semibold text-indigo-900 dark:text-indigo-100",
    subtitle: "text-sm text-indigo-600 dark:text-indigo-300 mt-1",
  },
  adminIndigoPurple: {
    wrapper: "p-6 border-b border-indigo-200 dark:border-indigo-700 bg-gradient-to-r from-indigo-100 to-purple-100 dark:from-indigo-900/40 dark:to-purple-900/40",
    icon: "w-6 h-6 text-indigo-600 dark:text-indigo-400",
    title: "text-xl font-semibold text-indigo-900 dark:text-indigo-100",
    subtitle: "text-sm text-indigo-600 dark:text-indigo-300 mt-1",
  },
  adminEmerald: {
    wrapper: "p-6 border-b border-emerald-200 dark:border-emerald-700 bg-gradient-to-r from-emerald-100 to-teal-100 dark:from-emerald-900/40 dark:to-teal-900/40",
    icon: "w-6 h-6 text-emerald-600 dark:text-emerald-400",
    title: "text-xl font-semibold text-emerald-900 dark:text-emerald-100",
    subtitle: "text-sm text-emerald-600 dark:text-emerald-300 mt-1",
  },
  adminAmber: {
    wrapper: "p-6 border-b border-amber-200 dark:border-amber-700 bg-gradient-to-r from-amber-100 to-orange-100 dark:from-amber-900/40 dark:to-orange-900/40",
    icon: "w-6 h-6 text-amber-600 dark:text-amber-400",
    title: "text-xl font-semibold text-amber-900 dark:text-amber-100",
    subtitle: "text-sm text-amber-600 dark:text-amber-300 mt-1",
  },
  adminRed: {
    wrapper: "p-6 border-b border-red-200 dark:border-red-700 bg-gradient-to-r from-red-100 to-orange-100 dark:from-red-900/40 dark:to-orange-900/40",
    icon: "w-6 h-6 text-red-600 dark:text-red-400",
    title: "text-xl font-semibold text-red-900 dark:text-red-100",
    subtitle: "text-sm text-red-600 dark:text-red-300 mt-1",
  },
  adminTeal: {
    wrapper: "p-6 border-b border-teal-200 dark:border-teal-700 bg-gradient-to-r from-teal-100 to-cyan-100 dark:from-teal-900/40 dark:to-cyan-900/40",
    icon: "w-6 h-6 text-teal-600 dark:text-teal-400",
    title: "text-xl font-semibold text-teal-900 dark:text-teal-100",
    subtitle: "text-sm text-teal-600 dark:text-teal-300 mt-1",
  },
  adminPurple: {
    wrapper: "p-6 border-b border-purple-200 dark:border-purple-700 bg-gradient-to-r from-purple-100 to-violet-100 dark:from-purple-900/40 dark:to-violet-900/40",
    icon: "w-6 h-6 text-purple-600 dark:text-purple-400",
    title: "text-xl font-semibold text-purple-900 dark:text-purple-100",
    subtitle: "text-sm text-purple-600 dark:text-purple-300 mt-1",
  },
  adminSky: {
    wrapper: "p-6 border-b border-sky-200 dark:border-sky-700 bg-gradient-to-r from-sky-100 to-cyan-100 dark:from-sky-900/40 dark:to-cyan-900/40",
    icon: "w-6 h-6 text-sky-600 dark:text-sky-400",
    title: "text-xl font-semibold text-sky-900 dark:text-sky-100",
    subtitle: "text-sm text-sky-600 dark:text-sky-300 mt-1",
  },
  adminSlate: {
    wrapper: "p-6 border-b border-slate-200 dark:border-slate-700 bg-gradient-to-r from-slate-100 to-gray-100 dark:from-slate-900/40 dark:to-gray-900/40",
    icon: "w-6 h-6 text-slate-600 dark:text-slate-400",
    title: "text-xl font-semibold text-slate-900 dark:text-slate-100",
    subtitle: "text-sm text-slate-600 dark:text-slate-300 mt-1",
  },
} as const;

export type SectionCardTone = keyof typeof TONES;

export interface SectionCardHeaderProps {
  tone: SectionCardTone;
  /** The `d` attribute of a single 24x24 Heroicons outline path. */
  iconPath: string;
  title: string;
  subtitle?: ReactNode;
  /** Docs path below the published docs root; renders a "Read the docs" link on the right. */
  docsPath?: string;
  /** Badge, button, or toggle group rendered on the right, before the docs link. */
  trailing?: ReactNode;
}

/**
 * Header strip of a settings/admin card: icon, title, subtitle on the left; optional trailing
 * controls and docs link on the right. Renders only the header — the card wrapper stays with the
 * caller so ids, refs, and borders remain where they are.
 */
export function SectionCardHeader({ tone, iconPath, title, subtitle, docsPath, trailing }: SectionCardHeaderProps) {
  const t = TONES[tone];
  const hasRight = trailing !== undefined || docsPath !== undefined;
  return (
    <div className={t.wrapper}>
      <div className="flex items-center justify-between gap-4">
        <div className="flex items-center space-x-2 min-w-0">
          <svg className={`${t.icon} flex-shrink-0`} fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d={iconPath} />
          </svg>
          <div className="min-w-0">
            <h2 className={t.title}>{title}</h2>
            {subtitle !== undefined && <p className={t.subtitle}>{subtitle}</p>}
          </div>
        </div>
        {hasRight && (
          <div className="flex flex-shrink-0 items-center gap-3">
            {trailing}
            {docsPath !== undefined && <DocsLink path={docsPath} />}
          </div>
        )}
      </div>
    </div>
  );
}
