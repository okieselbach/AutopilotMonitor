import { AuthGate } from "./AuthGate";
import { LandingNavbar } from "./LandingNavbar";
import { Hero } from "./Hero";
import { StatsBand } from "./StatsBand";
import { JustAsk } from "./JustAsk";
import { Story } from "./Story";
import { CapabilitiesStrip } from "./CapabilitiesStrip";
import { Comparison } from "./Comparison";
import { HowItWorks } from "./HowItWorks";
import { Testimonials } from "./Testimonials";
import { FinalCta } from "./FinalCta";
import { SiteFooter } from "../SiteFooter";
import { MarketingTracker } from "../MarketingTracker";
import type { PlatformStatsSnapshot } from "@/lib/platformStats";

/**
 * Landing page v2 — a scroll story of one enrollment.
 * All theming goes through the lp-* tokens (globals.css); sections live
 * in components/landing/. Auth behavior (AuthGate redirect, LoginButton
 * portal handoff) is unchanged from v1.
 *
 * Synchronous on purpose: app/page.tsx awaits the build-time stats and hands
 * them in, and the marketing guard tests render this body with the sync
 * renderToStaticMarkup.
 */
export function LandingPage({ statsSnapshot }: { statsSnapshot: PlatformStatsSnapshot | null }) {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)]">
      {/* Client component: handles auth redirect + loading overlay */}
      <AuthGate />
      <MarketingTracker page="landing">
        <LandingNavbar />
        <Hero />
        <StatsBand snapshot={statsSnapshot} />
        <JustAsk />
        <Story />
        <CapabilitiesStrip />
        <Comparison />
        <HowItWorks />
        <Testimonials />
        <FinalCta />
        <SiteFooter />
      </MarketingTracker>
    </div>
  );
}
