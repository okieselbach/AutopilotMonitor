"use client";

import { useEffect } from "react";
import { usePathname } from "next/navigation";
import {
  isOnPortalHost,
  isOnPublicHost,
  isPublicPath,
  PORTAL_HOST,
  PUBLIC_HOST,
} from "@/lib/hostRouting";

/**
 * Client-side replacement for the deleted middleware.ts host bounce (the
 * static export has no server): portal-only paths reaching www are sent to
 * portal, public paths reaching portal are sent back to www. The apex host is
 * a registrar-level 301 to www and never reaches this code.
 *
 * `location.replace` mirrors the middleware's 302 semantics — nothing is
 * cached, and the wrong-host entry does not linger in history. Dev (localhost)
 * and preview hosts match neither branch and pass through untouched.
 */
export function HostRoutingGuard() {
  const pathname = usePathname();

  useEffect(() => {
    if (!pathname) return;
    const { search, hash } = window.location;
    const suffix = `${pathname}${search}${hash}`;

    if (isOnPublicHost() && !isPublicPath(pathname)) {
      window.location.replace(`https://${PORTAL_HOST}${suffix}`);
    } else if (isOnPortalHost() && isPublicPath(pathname)) {
      window.location.replace(`https://${PUBLIC_HOST}${suffix}`);
    }
  }, [pathname]);

  return null;
}
