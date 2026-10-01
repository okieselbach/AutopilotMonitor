import { DOCS_URL } from "@/utils/config";
import { GITHUB_REPO_URL } from "@/utils/githubStars";
import { BrandMark } from "./BrandMark";
import { GitHubIcon } from "./GitHubIcon";

const LINK_COLUMNS = [
  {
    title: "Product",
    links: [
      { label: "The Story", href: "/#story", track: "story" },
      { label: "Capabilities", href: "/#features", track: "capabilities" },
      { label: "Compare", href: "/#comparison", track: "compare" },
      { label: "AI analysis", href: "/ai/", track: "ai" },
      { label: "Plans", href: "/plans", track: "plans" },
    ],
  },
  {
    title: "Resources",
    links: [
      { label: "Documentation", href: DOCS_URL, external: true, track: "docs" },
      { label: "Help & Support", href: "/help", track: "help" },
      { label: "Feedback", href: "https://github.com/okieselbach/AutopilotMonitor/issues", external: true, track: "feedback" },
      { label: "GitHub", href: GITHUB_REPO_URL, external: true, track: "github" },
    ],
  },
  {
    title: "Company",
    links: [
      { label: "About", href: "/about", track: "about" },
      { label: "glueckkanja AG", href: "https://www.glueckkanja.com", external: true, track: "glueckkanja" },
    ],
  },
  {
    title: "Legal",
    links: [
      { label: "Privacy Policy", href: "/privacy", track: "privacy" },
      { label: "Terms of Use", href: "/terms", track: "terms" },
      { label: "Imprint", href: "https://www.glueckkanja.com/en/imprint", external: true, track: "imprint" },
    ],
  },
];

/**
 * Shared footer for the public surface (landing, about, terms, privacy).
 * Uses lp-* tokens so it renders correctly in both themes everywhere.
 */
export function SiteFooter() {
  return (
    <footer className="border-t border-[var(--lp-line-soft)] bg-[var(--lp-surface)]">
      <div className="max-w-7xl mx-auto px-6 py-12">
        <div className="flex flex-col md:flex-row md:items-start gap-10">
          {/* Brand */}
          <div className="shrink-0 md:w-64">
            <div className="flex items-center gap-2.5 mb-3">
              <BrandMark className="w-6 h-6" />
              <span className="text-sm font-bold tracking-tight text-[var(--lp-ink)]">
                Autopilot Monitor
              </span>
            </div>
            <p className="text-xs text-[var(--lp-ink-faint)] leading-relaxed mb-4">
              Real-time monitoring, automated analysis, and AI-ready telemetry for Windows
              Autopilot enrollments.
            </p>
            <div className="flex items-center gap-3">
              <a
                href="https://www.linkedin.com/in/oliver-kieselbach/"
                data-track="footer_linkedin"
                target="_blank"
                rel="noopener noreferrer"
                className="text-[var(--lp-ink-faint)] hover:text-[var(--lp-accent-ink)] transition-colors"
                title="LinkedIn"
              >
                <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                  <path d="M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85 3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.062 2.062 0 01-2.063-2.065 2.064 2.064 0 112.063 2.065zm1.782 13.019H3.555V9h3.564v11.452zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0h.003z" />
                </svg>
              </a>
              <a
                href={GITHUB_REPO_URL}
                data-track="footer_github_icon"
                target="_blank"
                rel="noopener noreferrer"
                className="text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] transition-colors"
                title="GitHub"
              >
                <GitHubIcon className="w-4 h-4" />
              </a>
            </div>
          </div>

          {/* Link columns */}
          <div className="flex-1 grid grid-cols-2 sm:grid-cols-4 gap-8">
            {LINK_COLUMNS.map(column => (
              <div key={column.title}>
                <h4 className="text-[11px] font-semibold text-[var(--lp-ink)] uppercase tracking-wider mb-2.5">
                  {column.title}
                </h4>
                <ul className="space-y-1.5">
                  {column.links.map(link => (
                    <li key={link.label}>
                      <a
                        href={link.href}
                        {...(link.external ? { target: "_blank", rel: "noopener noreferrer" } : {})}
                        data-track={`footer_${link.track}`}
                        className="text-xs text-[var(--lp-ink-faint)] hover:text-[var(--lp-accent-ink)] transition-colors"
                      >
                        {link.label}
                      </a>
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </div>

        {/* Bottom bar */}
        <div className="mt-10 pt-5 border-t border-[var(--lp-line-soft)] text-center sm:text-left">
          <p className="text-[11px] text-[var(--lp-ink-faint)]">
            &copy; 2026 Autopilot Monitor. Built with ❤️ by Oliver Kieselbach.{" "}
            <span className="inline-block">Hosted on Azure by glueckkanja AG.</span>
          </p>
        </div>
      </div>
    </footer>
  );
}
