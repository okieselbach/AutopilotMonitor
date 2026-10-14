"use client";

import { Suspense, useEffect, useRef } from "react";
import { usePathname, useSearchParams } from "next/navigation";
import { WhatsNewPanel } from "./WhatsNewPanel";
import { useWhatsNewState, openWhatsNew } from "@/lib/whatsNewStore";
import { trackEvent } from "@/lib/appInsights";
import { isPublicPath } from "@/lib/hostRouting";
import { WHATS_NEW_CHANNELS, type WhatsNewChannel } from "@/lib/whatsNew";
import { useAuth } from "@/contexts/AuthContext";

/**
 * Mounts the What's new panel once, in the root layout, so the portal navbar, the landing
 * navbar and the Help page tile can all open the same instance through the module store.
 *
 * `?whats-new=platform|agent` opens it on load (release posts, LinkedIn links).
 *
 * The panel is styled with the landing tokens (`--lp-*`). Those are redefined by `.dark`
 * globally, which is what the portal wants — but the public site is light-only and forces
 * its tokens through `.landing-v2`. The panel portals to <body>, outside that wrapper, so on
 * public paths the class is re-applied here to keep the card as light as the page.
 */
export function WhatsNewPanelHost() {
  const { panelOpen } = useWhatsNewState();
  const pathname = usePathname();
  const onPublicPage = isPublicPath(pathname);

  return (
    <>
      {/* useSearchParams() needs a Suspense boundary for the static prerender. */}
      <Suspense fallback={null}>
        <WhatsNewDeepLink onPublicPage={onPublicPage} />
      </Suspense>
      {panelOpen && <WhatsNewPanel rootClassName={onPublicPage ? "landing-v2" : undefined} />}
    </>
  );
}

/**
 * Opens the panel once each time `?whats-new=` arrives, not only on the first mount: a portal
 * link opened without a session goes through the sign-in, which lands on the origin root and
 * reaches the link again by a client-side navigation (AuthGate), where this layout stays
 * mounted. A protected page waits for the session, so the panel neither flashes before the
 * sign-in redirect nor counts the open twice.
 */
function WhatsNewDeepLink({ onPublicPage }: { onPublicPage: boolean }) {
  const requested = useSearchParams()?.get("whats-new") ?? null;
  const { isAuthenticated, isLoading } = useAuth();
  const ready = onPublicPage || (isAuthenticated && !isLoading);
  const handled = useRef(false);

  useEffect(() => {
    if (requested === null) {
      handled.current = false;
      return;
    }
    if (handled.current || !ready) return;
    handled.current = true;
    const channel = (WHATS_NEW_CHANNELS as readonly string[]).includes(requested)
      ? (requested as WhatsNewChannel)
      : undefined;
    trackEvent("whats_new_opened", { source: "deeplink", channel: channel ?? "default" });
    openWhatsNew("deeplink", channel);
  }, [requested, ready]);

  return null;
}
