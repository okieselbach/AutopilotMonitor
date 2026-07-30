"use client";

import Link from "next/link";
import { useAuth } from "../../contexts/AuthContext";
import { useTheme } from "../../contexts/ThemeContext";
import { getPortalLoginUrl, shouldCrossOriginToPortal } from "../../lib/hostRouting";
import { DOCS_URL } from "@/utils/config";
import { BrandMark } from "../BrandMark";

const NAV_LINKS = [
  { href: "#story", label: "The Story" },
  { href: "#features", label: "Capabilities" },
  { href: "#comparison", label: "Compare" },
  { href: DOCS_URL, label: "Docs", external: true },
];

/**
 * Floating pill navigation for the landing page. Fixed, centered, glassy.
 * Same auth handoff behavior as PublicSiteNavbar: on www/apex, sign-in is
 * delegated to the portal origin so MSAL fires there.
 */
export function PillNavbar() {
  const { login, isAuthenticated } = useAuth();
  const { theme, toggleTheme } = useTheme();

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
    <div className="fixed top-3 inset-x-0 z-40 flex justify-center px-3 pointer-events-none">
      <nav className="pointer-events-auto flex items-center gap-1 sm:gap-2 h-12 pl-3 pr-2 rounded-full border border-[var(--lp-line)] bg-[var(--lp-nav)] backdrop-blur-xl shadow-lg shadow-black/5 max-w-full">
        <Link href="/" className="flex items-center gap-2 pr-1 sm:pr-2 shrink-0">
          <BrandMark className="w-5 h-5" />
          <span className="text-sm font-bold tracking-tight text-[var(--lp-ink)] whitespace-nowrap">
            Autopilot Monitor
          </span>
        </Link>

        <div className="hidden md:flex items-center">
          {NAV_LINKS.map(link =>
            link.external ? (
              <a
                key={link.label}
                href={link.href}
                target="_blank"
                rel="noopener noreferrer"
                className="px-3 py-1.5 text-[13px] font-medium rounded-full text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors whitespace-nowrap"
              >
                {link.label}
              </a>
            ) : (
              <a
                key={link.label}
                href={link.href}
                className="px-3 py-1.5 text-[13px] font-medium rounded-full text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors whitespace-nowrap"
              >
                {link.label}
              </a>
            )
          )}
        </div>

        <div className="flex items-center gap-1 sm:gap-1.5 shrink-0">
          <button
            onClick={toggleTheme}
            className="p-2 rounded-full text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
            title={theme === "dark" ? "Switch to light mode" : "Switch to dark mode"}
          >
            {theme === "dark" ? (
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364 6.364l-.707-.707M6.343 6.343l-.707-.707m12.728 0l-.707.707M6.343 17.657l-.707.707M16 12a4 4 0 11-8 0 4 4 0 018 0z" />
              </svg>
            ) : (
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646z" />
              </svg>
            )}
          </button>
          <a
            href="https://github.com/okieselbach/Autopilot-Monitor"
            target="_blank"
            rel="noopener noreferrer"
            className="p-2 rounded-full text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
            title="GitHub"
          >
            <svg className="w-4 h-4" viewBox="0 0 24 24" fill="currentColor">
              <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
            </svg>
          </a>
          <button
            onClick={handleSignIn}
            className="ml-0.5 px-4 py-1.5 rounded-full bg-[var(--lp-accent)] hover:brightness-105 text-white text-[13px] font-semibold shadow-sm transition-all whitespace-nowrap"
          >
            Sign In
          </button>
        </div>
      </nav>
    </div>
  );
}
