import { AuthGate } from "../components/landing/AuthGate";
import { PillNavbar } from "../components/landing/PillNavbar";
import { Hero } from "../components/landing/Hero";
import { Story } from "../components/landing/Story";
import { CapabilitiesStrip } from "../components/landing/CapabilitiesStrip";
import { Comparison } from "../components/landing/Comparison";
import { HowItWorks } from "../components/landing/HowItWorks";
import { FinalCta } from "../components/landing/FinalCta";
import { SiteFooter } from "../components/SiteFooter";

/**
 * Landing page v2 — a scroll story of one enrollment.
 * All theming goes through the lp-* tokens (globals.css); sections live
 * in components/landing/. Auth behavior (AuthGate redirect, LoginButton
 * portal handoff) is unchanged from v1.
 */
export default function LandingPage() {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)]">
      {/* Client component: handles auth redirect + loading overlay */}
      <AuthGate />
      <PillNavbar />
      <Hero />
      <Story />
      <CapabilitiesStrip />
      <Comparison />
      <HowItWorks />
      <FinalCta />
      <SiteFooter />
    </div>
  );
}
