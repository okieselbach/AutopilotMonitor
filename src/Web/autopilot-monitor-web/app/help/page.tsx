import { LandingNavbar } from "../../components/landing/LandingNavbar";
import { SiteFooter } from "../../components/SiteFooter";
import { DOCS_URL } from "@/utils/config";

const GITHUB_ISSUES = "https://github.com/okieselbach/AutopilotMonitor/issues";
const LINKEDIN_PROFILE = "https://www.linkedin.com/in/oliver-kieselbach/";
const GITHUB_ADVISORY = "https://github.com/okieselbach/AutopilotMonitor/security/advisories/new";

const SELF_SERVICE_LINKS = [
  {
    label: "Documentation",
    description: "Setup guides, concepts, and reference for every feature.",
    href: DOCS_URL,
  },
  {
    label: "FAQ",
    description: "Answers to the most common questions.",
    href: `${DOCS_URL}/troubleshooting/faq`,
  },
  {
    label: "Service Announcements",
    description: "Known issues and current service status.",
    href: `${DOCS_URL}/troubleshooting/service-announcements`,
  },
  {
    label: "Platform Changelog",
    description: "What changed recently in the portal and backend.",
    href: `${DOCS_URL}/changelog/platform-changelog`,
  },
];

export default function HelpPage() {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)]">
      <LandingNavbar />
      <header className="px-4 sm:px-6 lg:px-8 pt-14 sm:pt-16">
        <div className="max-w-5xl mx-auto">
          <p className="text-[11px] font-semibold uppercase tracking-[0.24em] text-[var(--lp-ink-faint)]">Support</p>
          <h1 className="mt-3 text-3xl sm:text-4xl font-bold tracking-tight text-[var(--lp-ink)]">
            Help &amp; Support
          </h1>
          <p className="mt-4 max-w-2xl text-[15px] text-gray-600 leading-relaxed">
            Autopilot Monitor is free and open source, and support is direct and personal — no
            ticket queues, no call centers. Pick the channel that fits and you&apos;ll hear back
            from the person who actually builds the platform.
          </p>
        </div>
      </header>

      <main className="max-w-5xl mx-auto px-4 sm:px-6 lg:px-8 py-10 space-y-10 text-[15px]">

        {/* Primary support channels */}
        <section className="grid grid-cols-1 sm:grid-cols-2 gap-5">
          <div className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-8 flex flex-col">
            <div className="flex items-center gap-3 mb-3">
              <span className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-[var(--lp-accent-soft)]">
                <svg className="w-5 h-5 text-[var(--lp-ink)]" fill="currentColor" viewBox="0 0 24 24">
                  <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
                </svg>
              </span>
              <h2 className="text-lg font-bold text-gray-900">GitHub Issues</h2>
            </div>
            <p className="text-sm text-gray-600 leading-relaxed mb-4 flex-1">
              The best channel for bug reports, feature requests, and questions. Issues are
              public, so other admins benefit from the answer too — chances are someone already
              reported what you&apos;re seeing.
            </p>
            <a
              href={GITHUB_ISSUES}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center justify-center gap-2 rounded-lg bg-[var(--lp-accent)] px-4 py-2.5 text-sm font-semibold text-white hover:opacity-90 transition-opacity"
            >
              Open a GitHub Issue
              <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
              </svg>
            </a>
          </div>

          <div className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-8 flex flex-col">
            <div className="flex items-center gap-3 mb-3">
              <span className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-[var(--lp-accent-soft)]">
                <svg className="w-5 h-5 text-[var(--lp-ink)]" fill="currentColor" viewBox="0 0 24 24">
                  <path d="M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85 3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.062 2.062 0 01-2.063-2.065 2.064 2.064 0 112.063 2.065zm1.782 13.019H3.555V9h3.564v11.452zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0h.003z" />
                </svg>
              </span>
              <h2 className="text-lg font-bold text-gray-900">LinkedIn</h2>
            </div>
            <p className="text-sm text-gray-600 leading-relaxed mb-4 flex-1">
              Prefer a direct, private conversation? Message Oliver Kieselbach on LinkedIn —
              for anything that shouldn&apos;t go into a public issue, such as tenant-specific
              details, or just to say hi.
            </p>
            <a
              href={LINKEDIN_PROFILE}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-[var(--lp-line)] bg-[var(--lp-surface)] px-4 py-2.5 text-sm font-semibold text-[var(--lp-ink)] hover:border-[var(--lp-ink-faint)] transition-colors"
            >
              Message on LinkedIn
              <svg className="w-3.5 h-3.5 text-[var(--lp-ink-faint)]" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
              </svg>
            </a>
          </div>
        </section>

        {/* How to report a problem */}
        <section className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-8">
          <h2 className="text-xl font-bold text-gray-900 mb-4">Something Wrong with an Enrollment?</h2>
          <p className="text-gray-700 leading-relaxed mb-4">
            If your question is about a specific enrollment, the easiest way is the{" "}
            <strong>Report Session</strong> button right in the portal&apos;s session view. It
            flags that session for analysis, and you can add a problem description and attach
            screenshots directly in the dialog — everything needed arrives in one place.
          </p>
          <p className="text-gray-700 leading-relaxed mb-4">
            For everything else — general questions, portal issues, or feature ideas — a GitHub
            issue works best. A short description of what you expected versus what happened, plus
            a screenshot where helpful, makes it much faster to track down. Attachments work in
            both places, so use whichever you prefer.
          </p>
          <p className="text-sm text-gray-500 leading-relaxed">
            GitHub issues are public — leave out anything confidential like tenant names, user
            identities, or internal hostnames. If the details are sensitive, use Report Session
            or LinkedIn instead and reference the issue number.
          </p>
        </section>

        {/* Self-service resources */}
        <section>
          <h2 className="text-xl font-bold text-gray-900 mb-2">Help Yourself First</h2>
          <p className="text-gray-600 mb-6 leading-relaxed">
            Many questions are already answered in the documentation — it&apos;s the fastest way
            to a solution.
          </p>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            {SELF_SERVICE_LINKS.map((link) => (
              <a
                key={link.label}
                href={link.href}
                target="_blank"
                rel="noopener noreferrer"
                className="rounded-xl border border-[var(--lp-line-soft)] bg-[var(--lp-surface)] px-5 py-4 hover:border-[var(--lp-accent-line)] hover:bg-[var(--lp-accent-soft)] transition-colors"
              >
                <span className="block text-sm font-semibold text-gray-800 mb-1">{link.label} →</span>
                <span className="block text-sm text-gray-600 leading-relaxed">{link.description}</span>
              </a>
            ))}
          </div>
        </section>

        {/* Security */}
        <section className="bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] rounded-xl p-8">
          <h2 className="text-xl font-bold text-gray-900 mb-4">Reporting a Security Issue</h2>
          <p className="text-gray-700 leading-relaxed">
            Please do not report security vulnerabilities in a public issue. Use a private{" "}
            <a
              href={GITHUB_ADVISORY}
              target="_blank"
              rel="noopener noreferrer"
              className="text-[var(--lp-accent-ink)] hover:opacity-80 underline"
            >
              GitHub security advisory
            </a>{" "}
            instead, so the issue can be assessed and fixed before details become public.
          </p>
        </section>

      </main>
      <SiteFooter />
    </div>
  );
}
