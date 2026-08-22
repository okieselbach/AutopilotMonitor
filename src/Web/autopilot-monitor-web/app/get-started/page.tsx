import type { Metadata } from "next";
import { LandingNavbar } from "../../components/landing/LandingNavbar";
import { LoginButton } from "../../components/landing/LoginButton";
import { SiteFooter } from "../../components/SiteFooter";
import { DOCS_URL, SITE_URL } from "@/utils/config";

export const metadata: Metadata = {
  title: "Get started",
  description:
    "From sign-in to your first live Autopilot enrollment in five steps: sign in, request access, grant consent, deploy the bootstrapper via Intune, watch live.",
  alternates: {
    canonical: `${SITE_URL}/get-started`,
  },
};

const STEPS = [
  {
    title: "Sign in with Microsoft",
    description:
      "Use your work account — there is no signup form. The first sign-in creates nothing in your tenant.",
    note: "30 seconds",
  },
  {
    title: "Tenant activation",
    description:
      "Every new organization goes through a short activation step — you'll see the status right after signing in, and it usually completes within a couple of minutes.",
    note: "Usually a few minutes",
  },
  {
    title: "Grant consent once",
    description:
      "Approve the Entra ID application for your tenant so the portal can operate. One admin consent, fully revocable.",
    note: "One-time admin consent",
  },
  {
    title: "Deploy the bootstrapper via Intune",
    description:
      "Assign one PowerShell script to your Autopilot scope. That is the whole rollout — no servers, no gateways, no infrastructure on your side.",
    note: "One script assignment",
  },
  {
    title: "Watch your first enrollment live",
    description:
      "The next device that enrolls streams in minutes later: phases, app installs, analysis results, diagnostics on demand.",
    note: "Minutes later",
  },
];

export default function GetStartedPage() {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)] flex flex-col">
      <LandingNavbar />

      <main className="flex-1 px-6 pt-16 sm:pt-20 pb-20">
        <div className="max-w-3xl mx-auto">
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">
            Get started
          </p>
          <h1 className="mt-4 text-4xl sm:text-5xl font-bold tracking-tight text-[var(--lp-ink)] leading-[1.08] text-balance">
            From sign-in to your first live enrollment.
          </h1>
          <p className="mt-4 text-lg text-[var(--lp-ink-soft)] leading-relaxed max-w-xl">
            Five steps, no infrastructure on your side. Most teams see their first live session
            the same day.
          </p>

          {/* Step sequence */}
          <div className="relative mt-12">
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

          {/* CTA */}
          <div className="mt-14 flex flex-wrap items-center gap-3">
            <LoginButton signup className="px-7 py-3 rounded-lg bg-[var(--lp-accent-ink)] hover:brightness-110 hover:shadow-lg text-white font-semibold shadow-md transition-all">
              Sign in to get started
            </LoginButton>
            <a
              href={DOCS_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="px-7 py-3 rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] text-[var(--lp-ink)] font-semibold hover:border-[var(--lp-ink-faint)] transition-colors"
            >
              Read the docs
            </a>
          </div>

          <p className="mt-6 text-sm text-[var(--lp-ink-faint)] leading-relaxed max-w-xl">
            When you need more later: the{" "}
            <a
              href="/plans"
              className="text-[var(--lp-accent-ink)] hover:opacity-80 underline"
            >
              Pro plan
            </a>{" "}
            adds SLAs, support commitments, MSP delegation, higher operating limits and more —
            same service, nothing to migrate.
          </p>
        </div>
      </main>

      <SiteFooter />
    </div>
  );
}
