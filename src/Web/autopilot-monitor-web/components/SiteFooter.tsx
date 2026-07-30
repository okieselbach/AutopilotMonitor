import { DOCS_URL } from "@/utils/config";
import { BrandMark } from "./BrandMark";

const LINK_COLUMNS = [
  {
    title: "Product",
    links: [
      { label: "The Story", href: "/#story" },
      { label: "Capabilities", href: "/#features" },
      { label: "Compare", href: "/#comparison" },
    ],
  },
  {
    title: "Resources",
    links: [
      { label: "Documentation", href: DOCS_URL, external: true },
      { label: "Feedback", href: "https://github.com/okieselbach/Autopilot-Monitor/issues", external: true },
      { label: "GitHub", href: "https://github.com/okieselbach/Autopilot-Monitor", external: true },
    ],
  },
  {
    title: "Company",
    links: [
      { label: "About", href: "/about" },
      { label: "glueckkanja AG", href: "https://www.glueckkanja.com", external: true },
    ],
  },
  {
    title: "Legal",
    links: [
      { label: "Privacy Policy", href: "/privacy" },
      { label: "Terms of Use", href: "/terms" },
      { label: "Imprint", href: "https://www.glueckkanja.com/en/imprint", external: true },
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
      <div className="max-w-6xl mx-auto px-6 py-12">
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
                href="https://github.com/okieselbach/Autopilot-Monitor"
                target="_blank"
                rel="noopener noreferrer"
                className="text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] transition-colors"
                title="GitHub"
              >
                <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                  <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
                </svg>
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
        <div className="mt-10 pt-5 border-t border-[var(--lp-line-soft)] flex flex-col sm:flex-row items-center justify-between gap-2">
          <p className="text-[11px] text-[var(--lp-ink-faint)]">
            &copy; 2026 Autopilot Monitor. Built with ❤️ by Oliver Kieselbach.{" "}
            <span className="inline-block">Hosted on Azure by glueckkanja AG.</span>
          </p>
          <p className="text-[11px] text-[var(--lp-ink-faint)]">
            Open source. Star us on{" "}
            <a
              href="https://github.com/okieselbach/Autopilot-Monitor"
              target="_blank"
              rel="noopener noreferrer"
              className="text-[var(--lp-ink-soft)] hover:text-[var(--lp-accent-ink)] transition-colors"
            >
              GitHub
            </a>
          </p>
        </div>
      </div>
    </footer>
  );
}
