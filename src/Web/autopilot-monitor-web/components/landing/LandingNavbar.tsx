"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { useAuth } from "../../contexts/AuthContext";
import { getPortalLoginUrl, shouldCrossOriginToPortal } from "../../lib/hostRouting";
import { DOCS_URL } from "@/utils/config";
import { BrandMark } from "../BrandMark";
import { GitHubIcon } from "../GitHubIcon";
import { GitHubStarLink } from "../GitHubStarLink";
import { GITHUB_REPO_URL } from "@/utils/githubStars";
import { useWhatsNew } from "@/hooks/useWhatsNew";

// Root-anchored (/#…) so the links also work from subpages
// like /get-started, /about, /terms, /privacy. `track` names the link in the
// marketing click events: nav_<track> in the bar, menu_<track> in the burger menu.
const NAV_LINKS = [
  { href: "/#story", label: "Product", track: "product" },
  { href: "/#features", label: "Capabilities", track: "capabilities" },
  { href: "/#comparison", label: "Compare", track: "compare" },
  { href: "/ai/", label: "AI", track: "ai" },
  { href: "/plans/", label: "Plans", track: "plans" },
  { href: DOCS_URL, label: "Docs", track: "docs", external: true },
];

/** With trailingSlash: true a page shows as /foo/ after client navigation and /foo on a hard load. */
function withoutTrailingSlash(path: string): string {
  return path.length > 1 ? path.replace(/\/+$/, "") : path;
}

function BellIcon({ className }: { className?: string }) {
  // Ringing bell: the public site announces "there is news" without claiming a count —
  // anonymous visitors have no seen mark, so a counter would read 9+ for everyone.
  return (
    <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      <path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9" />
      <path d="M13.73 21a2 2 0 0 1-3.46 0" />
      <path d="M2.5 8a10 10 0 0 1 2-4.5" />
      <path d="M21.5 8a10 10 0 0 0-2-4.5" />
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
  const whatsNew = useWhatsNew();
  const pathname = withoutTrailingSlash(usePathname() ?? "");
  // A route link (not an anchor, not external) whose page is the current one.
  const isCurrent = (link: (typeof NAV_LINKS)[number]) =>
    !link.external && !link.href.includes("#") && withoutTrailingSlash(link.href) === pathname;

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

  // Every <Link> here sets prefetch={false}: the App Router's viewport prefetch would
  // otherwise fetch the / and /get-started payloads on every landing visit, for a public
  // visitor who rarely navigates. (The portal chrome prefetches on hover intent instead,
  // see components/NavLink.tsx.)
  return (
    <nav className="sticky top-0 z-40 bg-[var(--lp-nav)] backdrop-blur-xl border-b border-[var(--lp-line-soft)]">
      {/* Measured widths (2026-10-01, six links, four-digit star count): the full desktop bar
          needs ~1070px at its xl spacing. It starts at lg, and between lg and xl it tightens the
          gaps, the link padding and the star label so it still fits at 1024px; below lg the burger
          menu carries links and sign-in. */}
      <div className="max-w-7xl mx-auto px-4 sm:px-6 h-16 flex items-center gap-4 lg:gap-6 xl:gap-8">
        <Link href="/" prefetch={false} data-track="nav_logo" className="flex items-center gap-2.5 shrink-0">
          <BrandMark className="w-6 h-6" />
          {/* Wordmark needs ~390px alongside GitHub mark + CTA + burger; mark alone below that */}
          <span className="hidden min-[390px]:block text-[15px] font-bold tracking-tight text-[var(--lp-ink)] whitespace-nowrap">
            Autopilot Monitor
          </span>
        </Link>

        <div className="hidden lg:flex items-center gap-1">
          {NAV_LINKS.map(link => {
            const current = isCurrent(link);
            return (
              <a
                key={link.label}
                href={link.href}
                {...(link.external ? { target: "_blank", rel: "noopener noreferrer" } : {})}
                aria-current={current ? "page" : undefined}
                data-track={`nav_${link.track}`}
                className={`px-2.5 xl:px-3 py-2 text-sm font-medium rounded-lg transition-colors ${
                  current
                    ? "text-[var(--lp-ink)] bg-[var(--lp-surface-2)]"
                    : "text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)]"
                }`}
              >
                {link.label}
              </a>
            );
          })}
        </div>

        <div className="ml-auto flex items-center gap-1 sm:gap-2 shrink-0">
          {/* Phones get the bare GitHub mark; the star pill with its counter starts at sm */}
          <a
            href={GITHUB_REPO_URL}
            target="_blank"
            rel="noopener noreferrer"
            data-track="nav_github"
            className="sm:hidden p-1.5 rounded-lg text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
            title="GitHub"
            aria-label="GitHub"
          >
            <GitHubIcon className="w-4 h-4" />
          </a>
          <div className="hidden sm:flex">
            <GitHubStarLink label="Star" track="nav_star" labelClassName="lg:max-xl:hidden" />
          </div>
          <button
            type="button"
            onClick={() => whatsNew.open("landing")}
            className="hidden lg:block p-2 rounded-lg text-[var(--lp-ink-faint)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
            title="What's new"
            aria-label="What's new"
          >
            <BellIcon className="w-4 h-4" />
          </button>
          <button
            onClick={handleSignIn}
            data-track="nav_sign_in"
            className="hidden lg:block px-3 py-2 text-sm font-semibold text-[var(--lp-ink)] hover:text-[var(--lp-accent-ink)] transition-colors"
          >
            Sign in
          </button>
          <Link
            href="/get-started"
            prefetch={false}
            data-track="nav_get_started"
            className="px-4 py-2 rounded-lg bg-[var(--lp-accent-ink)] hover:brightness-110 hover:shadow-md text-white text-sm font-semibold shadow-sm transition-all whitespace-nowrap"
          >
            Get started
          </Link>
          <button
            onClick={() => setMenuOpen(open => !open)}
            data-track={menuOpen ? "nav_menu_close" : "nav_menu_open"}
            className="lg:hidden p-2 -mr-2 rounded-lg text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
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
            className="lg:hidden absolute top-full left-0 right-0 h-screen bg-black/20"
            onClick={() => setMenuOpen(false)}
            aria-hidden="true"
          />
          <div
            id="landing-mobile-menu"
            className="lg:hidden absolute top-full left-0 right-0 bg-[var(--lp-surface)] border-b border-[var(--lp-line-soft)] shadow-lg"
          >
            <div className="px-6 py-4 flex flex-col gap-1">
              {NAV_LINKS.map(link => {
                const current = isCurrent(link);
                return (
                  <a
                    key={link.label}
                    href={link.href}
                    {...(link.external ? { target: "_blank", rel: "noopener noreferrer" } : {})}
                    onClick={() => setMenuOpen(false)}
                    aria-current={current ? "page" : undefined}
                    data-track={`menu_${link.track}`}
                    className={`px-3 py-2.5 text-[15px] font-medium rounded-lg transition-colors ${
                      current
                        ? "text-[var(--lp-ink)] bg-[var(--lp-surface-2)]"
                        : "text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)]"
                    }`}
                  >
                    {link.label}
                  </a>
                );
              })}
              <a
                href={GITHUB_REPO_URL}
                target="_blank"
                rel="noopener noreferrer"
                onClick={() => setMenuOpen(false)}
                data-track="menu_github"
                className="flex items-center gap-2.5 px-3 py-2.5 text-[15px] font-medium rounded-lg text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
              >
                <GitHubIcon className="w-4 h-4" />
                GitHub
              </a>
              <button
                type="button"
                onClick={() => { setMenuOpen(false); whatsNew.open("landing-menu"); }}
                className="flex items-center gap-2.5 px-3 py-2.5 text-[15px] font-medium rounded-lg text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)] hover:bg-[var(--lp-surface-2)] transition-colors"
              >
                <BellIcon className="w-4 h-4" />
                What&apos;s new
              </button>
              <div className="h-px bg-[var(--lp-line-soft)] my-2" />
              <button
                onClick={handleSignIn}
                data-track="menu_sign_in"
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
