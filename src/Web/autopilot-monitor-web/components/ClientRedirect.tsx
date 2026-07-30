"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import type { Route } from "next";

/**
 * Client-side replacement for server `redirect()` index pages — those are not
 * supported under `output: 'export'`. Renders nothing and replaces the history
 * entry on mount (same UX as the role-conditional shell in app/settings/page.tsx).
 */
export function ClientRedirect<T extends string>({ to }: { to: Route<T> }) {
  const router = useRouter();
  useEffect(() => {
    router.replace(to);
  }, [router, to]);
  return null;
}
