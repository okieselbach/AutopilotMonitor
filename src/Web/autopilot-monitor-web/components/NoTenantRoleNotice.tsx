"use client";

import { useEffect } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { trackEvent } from "@/lib/appInsights";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { DOCS_URL } from "@/lib/config";

/**
 * Full-page notice for a signed-in member without a tenant role who opened a page that needs one
 * (typically a shared session link). Every data call of such a page would answer 403, which used
 * to render as an empty but valid-looking page. Always the "member" wording: the link itself
 * proves that the organization uses Autopilot Monitor.
 */
export function NoTenantRoleNotice({ upn }: { upn: string }) {
  const pathname = usePathname();

  useEffect(() => {
    trackEvent("tenant_role_missing_shown", { path: pathname ?? "" });
  }, [pathname]);

  return (
    <div className="min-h-screen bg-[var(--lp-bg)] flex items-center justify-center p-4">
      <div className="bg-white rounded-lg shadow-xl p-8 max-w-md w-full text-center space-y-4">
        <svg className="h-12 w-12 text-amber-500 mx-auto" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5} aria-hidden="true">
          <path strokeLinecap="round" strokeLinejoin="round" d="M16.5 10.5V6.75a4.5 4.5 0 10-9 0v3.75m-.75 11.25h10.5a2.25 2.25 0 002.25-2.25v-6.75a2.25 2.25 0 00-2.25-2.25H6.75a2.25 2.25 0 00-2.25 2.25v6.75a2.25 2.25 0 002.25 2.25z" />
        </svg>
        <h2 className="text-xl font-semibold text-gray-900">You don&apos;t have access yet</h2>
        <p className="text-gray-600">
          You&apos;re signed in, but you have no role in your organization&apos;s Autopilot Monitor yet. Ask your
          Autopilot Monitor admin to add you under Access Management, then reload this page.
        </p>
        <p className="text-xs text-gray-400">Signed in as {upn}</p>
        <div className="flex flex-col items-center gap-3">
          <Link
            href="/progress"
            className="px-4 py-2 bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors"
          >
            Open Progress Portal
          </Link>
          <a
            href={`${DOCS_URL}${DOCS_PATHS.progressPortalOnly}`}
            target="_blank"
            rel="noopener noreferrer"
            className="text-sm text-green-700 underline hover:text-green-800"
          >
            Learn more
          </a>
        </div>
      </div>
    </div>
  );
}
