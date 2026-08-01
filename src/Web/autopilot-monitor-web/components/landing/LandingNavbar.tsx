"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
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

function GitHubIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="currentColor">
      <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
    </svg>
  );
}

/**
 * Full-width landing navigation. Same auth handoff behavior as
 * PublicSiteNavbar: on www/apex, sign-in is delegated to the portal
 * origin so MSAL fires there.
 */
export function LandingNavbar() {
  const { login, isAuthenticated } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);

  useEffect(() => {
    if (!menuOpen) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") setMenuOpen(false);
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [menuOpen]);

  const handleSignIn = () => {
    setMenuOpen(false);
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
      <div className="max-w-7xl mx-auto px-4 sm:px-6 h-16 flex items-center gap-8">
        <Link href="/" className="flex items-center gap-2.5 shrink-0">
          <BrandMark className="w-6 h-6" />
          {/* Wordmark needs ~360px alongside CTA + burger; mark alone below that */}
          <span className="hidden min-[360px]:block text-[15px] font-bold tracking-tight text-[var(--lp-ink)] whitespace-nowrap">
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
            href="https://github.com/okieselbach/AutopilotMonitor"
            target="_blank"
            rel="noopener noreferrer"
            className="hidden md:block p-2 rounded-lg text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
            title="GitHub"
          >
            <GitHubIcon className="w-4 h-4" />
          </a>
          <button
            onClick={handleSignIn}
            className="hidden md:block px-3 py-2 text-sm font-semibold text-[var(--lp-ink)] hover:text-[var(--lp-accent-ink)] transition-colors"
          >
            Sign in
          </button>
          <Link
            href="/get-started"
            className="px-4 py-2 rounded-lg bg-[var(--lp-accent-ink)] hover:brightness-110 hover:shadow-md text-white text-sm font-semibold shadow-sm transition-all whitespace-nowrap"
          >
            Get started
          </Link>
          <button
            onClick={() => setMenuOpen(open => !open)}
            className="md:hidden p-2 -mr-2 rounded-lg text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
            aria-expanded={menuOpen}
            aria-controls="landing-mobile-menu"
            aria-label={menuOpen ? "Close menu" : "Open menu"}
          >
            {menuOpen ? (
              <svg className="w-5 h-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <path d="M6 6l12 12M18 6L6 18" />
              </svg>
            ) : (
              <svg className="w-5 h-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <path d="M4 7h16M4 12h16M4 17h16" />
              </svg>
            )}
          </button>
        </div>
      </div>

      {menuOpen && (
        <>
          {/* nav's backdrop-blur makes it the containing block, so this stays
              absolute (not fixed) and stretches a viewport height below the bar */}
          <div
            className="md:hidden absolute top-full left-0 right-0 h-screen bg-black/20"
            onClick={() => setMenuOpen(false)}
            aria-hidden="true"
          />
          <div
            id="landing-mobile-menu"
            className="md:hidden absolute top-full left-0 right-0 bg-[var(--lp-surface)] border-b border-[var(--lp-line-soft)] shadow-lg"
          >
            <div className="px-6 py-4 flex flex-col gap-1">
              {NAV_LINKS.map(link => (
                <a
                  key={link.label}
                  href={link.href}
                  {...(link.external ? { target: "_blank", rel: "noopener noreferrer" } : {})}
                  onClick={() => setMenuOpen(false)}
                  className="px-3 py-2.5 text-[15px] font-medium rounded-lg text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
                >
                  {link.label}
                </a>
              ))}
              <a
                href="https://github.com/okieselbach/AutopilotMonitor"
                target="_blank"
                rel="noopener noreferrer"
                onClick={() => setMenuOpen(false)}
                className="flex items-center gap-2.5 px-3 py-2.5 text-[15px] font-medium rounded-lg text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
              >
                <GitHubIcon className="w-4 h-4" />
                GitHub
              </a>
              <div className="h-px bg-[var(--lp-line-soft)] my-2" />
              <button
                onClick={handleSignIn}
                className="w-full px-4 py-2.5 rounded-lg border border-[var(--lp-line)] text-sm font-semibold text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
              >
                Sign in
              </button>
            </div>
          </div>
        </>
      )}
    </nav>
  );
}
