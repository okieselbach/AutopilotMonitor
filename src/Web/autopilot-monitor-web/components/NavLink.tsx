"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, type ComponentProps } from "react";
import type { Route } from "next";

type NavLinkProps = Omit<ComponentProps<typeof Link>, "prefetch">;

// Routes already requested in this page life. Next's segment cache dedupes as well; this only
// keeps repeated hovers free of work.
const requested = new Set<string>();

/**
 * Navigation link with prefetch on intent.
 *
 * `prefetch={false}` keeps the App Router's viewport prefetch off — and with it the built-in
 * hover prefetch, which the router disables together with it. The route payload is requested
 * when the user shows intent instead (hover, keyboard focus, touch). Since the static export
 * the payload is a static file on the edge, so the request costs nothing server-side, and it
 * usually completes inside the hover→click gap (measured 2026-09-23: without it the payload
 * fetch was the serial 319 ms in front of every first navigation to a route).
 * Rationale and history: internal/docs/web/portal-navigation-prefetch.md.
 */
export function NavLink({ href, onMouseEnter, onFocus, onTouchStart, ...rest }: NavLinkProps) {
  const router = useRouter();

  const prefetchOnIntent = useCallback(() => {
    const target = typeof href === "string" ? href : (href.pathname ?? null);
    if (!target || target === "#" || requested.has(target)) return;
    requested.add(target);
    // A UrlObject pathname is a plain string; typed routes want a Route. Prefetching an
    // unknown path is harmless (the router ignores a miss), so the cast is safe here.
    router.prefetch(target as Route);
  }, [href, router]);

  return (
    <Link
      href={href}
      prefetch={false}
      onMouseEnter={(e) => {
        onMouseEnter?.(e);
        prefetchOnIntent();
      }}
      onFocus={(e) => {
        onFocus?.(e);
        prefetchOnIntent();
      }}
      onTouchStart={(e) => {
        onTouchStart?.(e);
        prefetchOnIntent();
      }}
      {...rest}
    />
  );
}
