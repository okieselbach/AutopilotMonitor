import type { Metadata } from "next";
import Link from "next/link";
import { LandingNavbar } from "../../components/landing/LandingNavbar";
import { SignupConsentCta } from "../../components/landing/SignupConsentCta";
import { SiteFooter } from "../../components/SiteFooter";
import { MarketingTracker } from "../../components/MarketingTracker";
import { DOCS_URL, SITE_URL } from "@/lib/config";

export const metadata: Metadata = {
  title: "Get started",
  description:
    "From sign-in to your first live Autopilot enrollment in four steps: sign in, follow the setup guide, add one script in Intune, watch live.",
  alternates: {
    canonical: `${SITE_URL}/get-started`,
  },
};

const STEPS = [
  {
    title: "Sign in with Microsoft",
    description: "Use your work account. Your tenant is usually ready a minute or two later.",
    note: "No signup form",
  },
  {
    title: "Follow the setup guide",
    description:
      "A checklist on your dashboard: recommended settings in one click, your hardware models, one admin consent.",
    note: "A few minutes",
  },
  {
    title: "Add one script in Intune",
    description: "Assign one PowerShell script to your Autopilot devices. No servers, no gateways.",
    note: "The whole rollout",
  },
  {
    title: "Watch your first enrollment live",
    description: "Enroll a device and follow it on your dashboard as it happens: phases, app installs, errors.",
    note: "In real time",
  },
];

export default function GetStartedPage() {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)] flex flex-col">
      <MarketingTracker page="get_started">
        <LandingNavbar />

        <main className="flex-1 px-6 pt-16 sm:pt-20 pb-20">
          <div className="max-w-3xl mx-auto">
            <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">
              Get started
            </p>
            <h1 className="mt-4 text-4xl sm:text-5xl font-bold tracking-tight text-[var(--lp-ink)] leading-[1.08] text-balance">
              From <span className="whitespace-nowrap">sign-in</span> to your first live enrollment.
            </h1>
            <p className="mt-4 text-lg text-[var(--lp-ink-soft)] leading-relaxed max-w-xl">
              Four steps, a few minutes, no infrastructure on your side.
            </p>

            {/* Step sequence */}
            <div data-track-section="steps" className="relative mt-12">
              <div className="absolute left-[15px] top-3 bottom-3 w-px bg-[var(--lp-line)]" aria-hidden="true" />
              <ol className="space-y-8">
                {STEPS.map((step, i) => (
                  <li key={step.title} className="relative pl-14">
                    <span className="absolute left-0 top-0 w-8 h-8 rounded-full bg-[var(--lp-accent)] text-white text-sm font-bold flex items-center justify-center">
                      {i + 1}
                    </span>
                    <div className="flex items-baseline gap-3 flex-wrap">
                      <h2 className="text-lg font-semibold text-[var(--lp-ink)]">{step.title}</h2>
                      <span className="text-[11px] font-medium px-2 py-0.5 rounded-full bg-[var(--lp-accent-soft)] text-[var(--lp-accent-ink)]">
                        {step.note}
                      </span>
                    </div>
                    <p className="mt-1.5 text-[15px] text-[var(--lp-ink-soft)] leading-relaxed">{step.description}</p>
                  </li>
                ))}
              </ol>
            </div>

            {/* CTA — sign-in waits for the Terms + DPA tick */}
            <SignupConsentCta>
              <a
                href={DOCS_URL}
                data-track="get_started_docs"
                target="_blank"
                rel="noopener noreferrer"
                className="px-7 py-3 rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] text-[var(--lp-ink)] font-semibold hover:border-[var(--lp-ink-faint)] transition-colors"
              >
                Read the docs
              </a>
            </SignupConsentCta>

            <p className="mt-6 text-sm text-[var(--lp-ink-faint)] leading-relaxed max-w-xl">
              When you need more later: the{" "}
              <Link
                href="/plans"
                prefetch={false}
                data-track="get_started_pro_plan"
                className="text-[var(--lp-accent-ink)] hover:opacity-80 underline"
              >
                Pro plan
              </Link>{" "}
              adds SLAs, support commitments, MSP delegation, higher operating limits and more —
              same service, nothing to migrate.
            </p>
          </div>
        </main>

        <SiteFooter />
      </MarketingTracker>
    </div>
  );
}
