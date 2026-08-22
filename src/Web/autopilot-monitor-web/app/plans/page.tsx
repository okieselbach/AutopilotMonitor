import Link from "next/link";
import { LandingNavbar } from "../../components/landing/LandingNavbar";
import { SiteFooter } from "../../components/SiteFooter";
import { PlanCards } from "../../components/plans/PlanCards";
import { DOCS_URL } from "@/utils/config";

/**
 * Public plans page. Deliberately named "Plans" (not "Pricing"): the message is
 * free-first — Community is the full product and stays free; Pro is the optional
 * step up. The cards are the shared PlanCards, so this page can never drift from
 * the portal's Plan section.
 */
export default function PlansPage() {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)]">
      <LandingNavbar />
      <header className="px-4 sm:px-6 lg:px-8 pt-14 sm:pt-16">
        <div className="max-w-5xl mx-auto">
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">Plans</p>
          <h1 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)]">
            The full product is free — and stays free.
          </h1>
          <p className="mt-4 max-w-2xl text-[15px] text-gray-600 leading-relaxed">
            Community is not a trial and not a cut-down teaser: live monitoring, the full rules
            engine, fleet analytics, and AI integration are free for everyone. Pro is the optional
            step up for organizations that want longer retention, higher limits, MSP delegation,
            and reliability commitments.
          </p>
        </div>
      </header>

      <main className="max-w-5xl mx-auto px-4 sm:px-6 lg:px-8 py-10 space-y-8 text-[15px]">
        <PlanCards
          surface="marketing"
          communityBadge={
            <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-semibold bg-[var(--lp-accent-soft)] text-[var(--lp-accent-ink)]">
              Free forever
            </span>
          }
          proBadge={
            <span className="inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-purple-100 text-purple-700 border border-purple-200">
              Coming soon
            </span>
          }
          proCta={
            <Link
              href="/buy"
              className="block w-full text-center text-sm font-semibold text-white bg-purple-600 rounded-lg px-4 py-2.5 hover:bg-purple-700 transition-colors"
            >
              How to get Pro
            </Link>
          }
        />

        <p className="text-sm text-gray-600">
          Pricing and availability for Pro will be announced. Community stays free.{" "}
          For a feature-by-feature reference, see the{" "}
          <a
            href={`${DOCS_URL}/plans`}
            target="_blank"
            rel="noopener noreferrer"
            className="text-[var(--lp-accent-ink)] hover:opacity-80 underline"
          >
            plans page in the documentation
          </a>
          .
        </p>
      </main>
      <SiteFooter />
    </div>
  );
}
