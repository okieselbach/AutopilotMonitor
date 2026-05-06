import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";

/**
 * Host-based routing for the public/portal split.
 *
 *   www.autopilotmonitor.com    → public/marketing surface (SEO-indexed)
 *   portal.autopilotmonitor.com → authenticated app surface
 *   autopilotmonitor.com (apex) → 301 to www. (handled by Strato HTTP
 *                                  forwarder before traffic reaches us;
 *                                  the apex branch below is defense-in-depth
 *                                  in case DNS ever bypasses Strato).
 *
 * Public paths reaching portal. are bounced back to www., and authenticated
 * paths reaching www. are forwarded to portal. We use 302 for the
 * www↔portal swaps to keep the migration reversible — browsers cache 301/308
 * indefinitely. Apex → www is 301 because that destination is permanent.
 *
 * In dev (localhost) and preview environments the host matches none of the
 * branches, so the middleware passes through unchanged.
 */

const PUBLIC_HOST = "www.autopilotmonitor.com";
const PORTAL_HOST = "portal.autopilotmonitor.com";
const APEX_HOST = "autopilotmonitor.com";

const PUBLIC_PATH_PREFIXES = [
  "/about",
  "/changelog",
  "/docs",
  "/privacy",
  "/terms",
  "/roadmap",
  "/sla",
  "/go",
  "/robots.txt",
  "/sitemap.xml",
  "/IndexNow.txt",
  "/opengraph-image",
  "/twitter-image",
  "/apple-icon",
  "/icon",
];

function isPublicPath(pathname: string): boolean {
  if (pathname === "/") return true;
  for (const p of PUBLIC_PATH_PREFIXES) {
    if (pathname === p) return true;
    if (pathname.startsWith(p + "/")) return true;
    // Match generated assets like /opengraph-image.png, /icon-192.png.
    if (pathname.startsWith(p + ".") || pathname.startsWith(p + "-")) return true;
  }
  return false;
}

export function middleware(req: NextRequest) {
  const host = (req.headers.get("host") ?? "").toLowerCase();
  const { pathname, search } = req.nextUrl;

  if (host === APEX_HOST) {
    return NextResponse.redirect(
      `https://${PUBLIC_HOST}${pathname}${search}`,
      301,
    );
  }

  if (host === PUBLIC_HOST && !isPublicPath(pathname)) {
    return NextResponse.redirect(
      `https://${PORTAL_HOST}${pathname}${search}`,
      302,
    );
  }

  if (host === PORTAL_HOST && isPublicPath(pathname)) {
    return NextResponse.redirect(
      `https://${PUBLIC_HOST}${pathname}${search}`,
      302,
    );
  }

  return NextResponse.next();
}

export const config = {
  matcher: [
    // Skip Next internals and the static favicon. Everything else — including
    // route handlers under /api/* and /go/[code] — runs through middleware.
    "/((?!_next/static|_next/image|favicon.ico).*)",
  ],
};
