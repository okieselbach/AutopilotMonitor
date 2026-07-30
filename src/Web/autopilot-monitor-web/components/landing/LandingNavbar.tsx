"use client";

import Link from "next/link";
import { useAuth } from "../../contexts/AuthContext";
import { getPortalLoginUrl, shouldCrossOriginToPortal } from "../../lib/hostRouting";
import { DOCS_URL } from "@/utils/config";
import { BrandMark } from "../BrandMark";

// Root-anchored (/#…) so the links also work from subpages
// like /get-started, /about, /terms, /privacy.
const NAV_LINKS = [
  { href: "/#story", label: "Product" },
  { href: "/#features", label: "Capabilities" },
  { href: "/#comparison", label: "Compare" },
  { href: DOCS_URL, label: "Docs", external: true },
];

/**
 * Full-width landing navigation. Same auth handoff behavior as
 * PublicSiteNavbar: on www/apex, sign-in is delegated to the portal
 * origin so MSAL fires there.
 */
export function LandingNavbar() {
  const { login, isAuthenticated } = useAuth();

  const handleSignIn = () => {
    if (shouldCrossOriginToPortal()) {
      window.location.href = getPortalLoginUrl();
      return;
    }
    void login();
  };

  // Logged-in users only see the authenticated app navbar.
  if (isAuthenticated) {
    return null;
  }

  return (
    <nav className="sticky top-0 z-40 bg-[var(--lp-nav)] backdrop-blur-xl border-b border-[var(--lp-line-soft)]">
      <div className="max-w-7xl mx-auto px-6 h-16 flex items-center gap-8">
        <Link href="/" className="flex items-center gap-2.5 shrink-0">
          <BrandMark className="w-6 h-6" />
          <span className="text-[15px] font-bold tracking-tight text-[var(--lp-ink)] whitespace-nowrap">
            Autopilot Monitor
          </span>
        </Link>

        <div className="hidden md:flex items-center gap-1">
          {NAV_LINKS.map(link =>
            link.external ? (
              <a
                key={link.label}
                href={link.href}
                target="_blank"
                rel="noopener noreferrer"
                className="px-3 py-2 text-sm font-medium rounded-lg text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
              >
                {link.label}
              </a>
            ) : (
              <a
                key={link.label}
                href={link.href}
                className="px-3 py-2 text-sm font-medium rounded-lg text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
              >
                {link.label}
              </a>
            )
          )}
        </div>

        <div className="ml-auto flex items-center gap-1.5 sm:gap-2 shrink-0">
          <a
            href="https://github.com/okieselbach/Autopilot-Monitor"
            target="_blank"
            rel="noopener noreferrer"
            className="p-2 rounded-lg text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
            title="GitHub"
          >
            <svg className="w-4 h-4" viewBox="0 0 24 24" fill="currentColor">
              <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
            </svg>
          </a>
          <button
            onClick={handleSignIn}
            className="hidden sm:block px-3 py-2 text-sm font-semibold text-[var(--lp-ink)] hover:text-[var(--lp-accent-ink)] transition-colors"
          >
            Sign in
          </button>
          <Link
            href="/get-started"
            className="px-4 py-2 rounded-lg bg-[var(--lp-accent)] hover:brightness-105 hover:shadow-md text-white text-sm font-semibold shadow-sm transition-all whitespace-nowrap"
          >
            Get started
          </Link>
        </div>
      </div>
    </nav>
  );
}
