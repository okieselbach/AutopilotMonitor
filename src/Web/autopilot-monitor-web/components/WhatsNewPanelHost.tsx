"use client";

import { useEffect } from "react";
import { usePathname } from "next/navigation";
import { WhatsNewPanel } from "./WhatsNewPanel";
import { useWhatsNewState, openWhatsNew } from "@/lib/whatsNewStore";
import { trackEvent } from "@/lib/appInsights";
import { isPublicPath } from "@/lib/hostRouting";
import { WHATS_NEW_CHANNELS, type WhatsNewChannel } from "@/lib/whatsNew";

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

  useEffect(() => {
    const requested = new URLSearchParams(window.location.search).get("whats-new");
    if (requested === null) return;
    const channel = (WHATS_NEW_CHANNELS as readonly string[]).includes(requested)
      ? (requested as WhatsNewChannel)
      : undefined;
    trackEvent("whats_new_opened", { source: "deeplink", channel: channel ?? "default" });
    openWhatsNew("deeplink", channel);
  }, []);

  if (!panelOpen) return null;
  return <WhatsNewPanel rootClassName={onPublicPage ? "landing-v2" : undefined} />;
}
