import { LandingNavbar } from "../../components/landing/LandingNavbar";
import { SiteFooter } from "../../components/SiteFooter";
import { MarketingTracker } from "../../components/MarketingTracker";
import { AiHero } from "../../components/ai/AiHero";
import { StructuredFacts } from "../../components/ai/StructuredFacts";
import { QuestionsSection } from "../../components/ai/QuestionsSection";
import { ArchitectureSection } from "../../components/ai/ArchitectureSection";
import { ConnectSection } from "../../components/ai/ConnectSection";
import { ClosingCta } from "../../components/ai/ClosingCta";

/**
 * AI analysis with MCP: what you can ask, where the model runs, how to connect. Every claim on the
 * page must hold against the MCP access model and the customer docs (D-305).
 */
export default function AiPage() {
  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)]">
      <MarketingTracker page="ai">
        <LandingNavbar />
        <main>
          <AiHero />
          <StructuredFacts />
          <QuestionsSection />
          <ArchitectureSection />
          <ConnectSection />
          <ClosingCta />
        </main>
        <SiteFooter />
      </MarketingTracker>
    </div>
  );
}
