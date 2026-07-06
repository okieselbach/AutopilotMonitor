import { notFound } from "next/navigation";
import type { Metadata } from "next";
import { NAV_SECTIONS, type SectionId } from "../docsNavSections";
import { McpDocsGate } from "../McpDocsGate";
import { GlobalAdminDocsGate } from "../GlobalAdminDocsGate";
import { SectionPrivatePreview } from "../sections/SectionPrivatePreview";
import { SectionGeneral } from "../sections/SectionGeneral";
import { SectionOverview } from "../sections/SectionOverview";
import { SectionSetup } from "../sections/SectionSetup";
import { SectionAgent } from "../sections/SectionAgent";
import { SectionAgentSetup } from "../sections/SectionAgentSetup";
import { SectionSettings } from "../sections/SectionSettings";
import { SectionGatherRules } from "../sections/SectionGatherRules";
import { SectionAnalyzeRules } from "../sections/SectionAnalyzeRules";
import { SectionImeLogPatterns } from "../sections/SectionImeLogPatterns";
import { SectionFaq } from "../sections/SectionFaq";
import { SectionAgentChangelog } from "../sections/SectionAgentChangelog";
import { SectionKnownIssues } from "../sections/SectionKnownIssues";

const SECTION_COMPONENTS: Record<string, React.ComponentType> = {
  "private-preview": SectionPrivatePreview,
  "general":         SectionGeneral,
  "overview":        SectionOverview,
  "setup":           SectionSetup,
  "agent":           SectionAgent,
  "agent-setup":     SectionAgentSetup,
  "settings":        SectionSettings,
  "gather-rules":    SectionGatherRules,
  "analyze-rules":   SectionAnalyzeRules,
  "ime-log-patterns": SectionImeLogPatterns,
  "faq":              SectionFaq,
  "known-issues":     SectionKnownIssues,
  "agent-changelog":  SectionAgentChangelog,
};

function isMcpSection(id: string): boolean {
  return NAV_SECTIONS.some((s) => s.id === id && s.requiresMcpAccess);
}

function isGlobalAdminSection(id: string): boolean {
  return NAV_SECTIONS.some((s) => s.id === id && s.requiresGlobalAdmin);
}

export function generateStaticParams() {
  return NAV_SECTIONS.map((s) => ({ section: s.id }));
}

type Props = { params: Promise<{ section: string }> };

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { section } = await params;
  const nav = NAV_SECTIONS.find((s) => s.id === section);
  if (!nav) return {};

  return {
    title: nav.label,
    description: nav.description,
    openGraph: {
      title: `${nav.label} | AutopilotMonitor Docs`,
      description: nav.description,
      url: `https://www.autopilotmonitor.com/docs/${section}`,
    },
    twitter: {
      title: `${nav.label} | AutopilotMonitor Docs`,
      description: nav.description,
    },
    alternates: {
      canonical: `https://www.autopilotmonitor.com/docs/${section}`,
    },
  };
}

export default async function DocsSectionPage({ params }: Props) {
  const { section } = await params;
  const nav = NAV_SECTIONS.find((s) => s.id === section);
  if (!nav) notFound();

  // Gated sections are rendered entirely client-side via their respective gates
  // (dynamic import — no HTML shipped until auth check passes).
  const isMcp = isMcpSection(section);
  const isAdmin = isGlobalAdminSection(section);
  if (!isMcp && !isAdmin && !SECTION_COMPONENTS[section]) notFound();

  const SectionContent = (isMcp || isAdmin) ? null : SECTION_COMPONENTS[section]!;

  const breadcrumbJsonLd = {
    "@context": "https://schema.org",
    "@type": "BreadcrumbList",
    itemListElement: [
      { "@type": "ListItem", position: 1, name: "Home", item: "https://www.autopilotmonitor.com" },
      { "@type": "ListItem", position: 2, name: "Docs", item: "https://www.autopilotmonitor.com/docs" },
      { "@type": "ListItem", position: 3, name: nav.label, item: `https://www.autopilotmonitor.com/docs/${section}` },
    ],
  };

  return (
    <>
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(breadcrumbJsonLd) }}
      />
      {isMcp ? <McpDocsGate sectionId={section} /> : isAdmin ? <GlobalAdminDocsGate sectionId={section} /> : SectionContent && <SectionContent />}
    </>
  );
}
