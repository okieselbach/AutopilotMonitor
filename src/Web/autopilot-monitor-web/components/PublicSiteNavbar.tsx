"use client";

import Link from "next/link";
import { useAuth } from "../contexts/AuthContext";
import { useTheme } from "../contexts/ThemeContext";
import { getPortalLoginUrl, shouldCrossOriginToPortal } from "../lib/hostRouting";

export function PublicSiteNavbar({ showSectionLinks, fullWidth = false }: { showSectionLinks: boolean; fullWidth?: boolean }) {
  const { login, isAuthenticated } = useAuth();
  const { theme, toggleTheme } = useTheme();
  const isDark = theme === "dark";

  // On www/apex, hand sign-in off to portal so MSAL fires on the right
  // origin and the token lands in portal's sessionStorage directly.
  const handleSignIn = () => {
    if (shouldCrossOriginToPortal()) {
      window.location.href = getPortalLoginUrl();
      return;
    }
    void login();
  };

  // Logged-in users should only see the main authenticated app navbar.
  if (isAuthenticated) {
    return null;
  }

  return (
    <nav
      className={`sticky top-0 z-30 backdrop-blur-md border-b shadow-sm ${
        isDark ? "bg-slate-900/85 border-slate-700/70" : "bg-white/80 border-gray-200/60"
      }`}
    >
      <div className={`${fullWidth ? "" : "max-w-7xl mx-auto"} px-4 sm:px-6 flex items-center justify-between h-14`}>
        <Link href="/" className="flex items-center space-x-2.5 shrink-0">
          <div className="w-8 h-8 bg-gradient-to-br from-blue-600 to-indigo-600 rounded-lg flex items-center justify-center">
            <svg className="w-5 h-5 text-white" viewBox="0 0 24 24" fill="none">
              <rect x="5.0" y="12.2" width="2.8" height="7.8" rx="0.9" fill="currentColor" />
              <rect x="10.6" y="10.9" width="2.8" height="9.1" rx="0.9" fill="currentColor" />
              <rect x="16.2" y="8.6" width="2.8" height="11.4" rx="0.9" fill="currentColor" />
              <path d="M4.4 8.9L8.6 6.8L12.0 7.4L15.4 5.5L18.8 4.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
              <path d="M17.8 4.2L19.1 4.9L17.9 5.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </div>
          <span className="text-lg font-bold bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent">
            <span className="hidden md:inline">AutopilotMonitor</span>
            <span className="md:hidden">AP Monitor</span>
          </span>
        </Link>

        {showSectionLinks ? (
          <div className="hidden sm:flex items-center gap-1">
            <a
              href="#features"
              className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors ${
                isDark
                  ? "bg-transparent text-gray-300 hover:text-gray-100 hover:bg-white/5"
                  : "text-gray-500 hover:text-blue-600 hover:bg-blue-50/50"
              }`}
            >
              Features
            </a>
            <a
              href="#how-it-works"
              className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors ${
                isDark
                  ? "bg-transparent text-gray-300 hover:text-gray-100 hover:bg-white/5"
                  : "text-gray-500 hover:text-blue-600 hover:bg-blue-50/50"
              }`}
            >
              How It Works
            </a>
            <a
              href="#comparison"
              className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors ${
                isDark
                  ? "bg-transparent text-gray-300 hover:text-gray-100 hover:bg-white/5"
                  : "text-gray-500 hover:text-blue-600 hover:bg-blue-50/50"
              }`}
            >
              Comparison
            </a>
            <a
              href="https://docs.autopilotmonitor.com"
              target="_blank"
              rel="noopener noreferrer"
              className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors ${
                isDark
                  ? "bg-transparent text-gray-300 hover:text-gray-100 hover:bg-white/5"
                  : "text-gray-500 hover:text-blue-600 hover:bg-blue-50/50"
              }`}
            >
              Docs
            </a>
          </div>
        ) : (
          <div />
        )}

        <div className="flex items-center gap-2 shrink-0">
          <button
            onClick={toggleTheme}
            className={`p-1.5 rounded-lg transition-colors ${
              isDark ? "text-gray-300 hover:bg-white/10" : "text-gray-500 hover:bg-gray-100"
            }`}
            title={theme === "dark" ? "Switch to light mode" : "Switch to dark mode"}
          >
            {theme === "dark" ? (
              <svg className="w-4 h-4 text-yellow-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364 6.364l-.707-.707M6.343 6.343l-.707-.707m12.728 0l-.707.707M6.343 17.657l-.707.707M16 12a4 4 0 11-8 0 4 4 0 018 0z" />
              </svg>
            ) : (
              <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646z" />
              </svg>
            )}
          </button>
          <a
            href="https://docs.autopilotmonitor.com"
            target="_blank"
            rel="noopener noreferrer"
            className={`sm:hidden p-1.5 rounded-lg transition-colors ${
              isDark ? "text-gray-300 hover:bg-white/10" : "text-gray-500 hover:bg-gray-100"
            }`}
            title="Documentation"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" />
            </svg>
          </a>
          <a
            href="https://github.com/okieselbach/Autopilot-Monitor"
            target="_blank"
            rel="noopener noreferrer"
            className={`inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium rounded-full border transition-colors ${
              isDark
                ? "text-gray-300 border-slate-600 hover:border-slate-500 hover:bg-white/5"
                : "text-gray-600 border-gray-200 hover:border-gray-300 hover:bg-gray-50"
            }`}
          >
            <svg className="w-4 h-4" viewBox="0 0 24 24" fill="currentColor">
              <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
            </svg>
            <span className="hidden sm:inline">GitHub</span>
          </a>
          <button
            onClick={handleSignIn}
            className="px-4 py-1.5 bg-gradient-to-r from-blue-600 to-indigo-600 text-white rounded-lg text-sm font-semibold shadow-sm hover:shadow-md transition-all"
          >
            Sign In
          </button>
        </div>
      </div>
    </nav>
  );
}
