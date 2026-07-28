"use client";

import { notFound } from "next/navigation";
import { type AgentSectionId } from "../agentNavSections";
import { SectionAgentSettings } from "../sections/SectionAgentSettings";
import { SectionAgentAnalyzers } from "../sections/SectionAgentAnalyzers";
import { SectionDiagnostics } from "../sections/SectionDiagnostics";
import { SectionUnrestrictedMode } from "../sections/SectionUnrestrictedMode";

const SECTION_COMPONENTS: Record<AgentSectionId, React.ComponentType> = {
  "settings": SectionAgentSettings,
  "analyzers": SectionAgentAnalyzers,
  "diagnostics": SectionDiagnostics,
  "unrestricted-mode": SectionUnrestrictedMode,
};

export function SectionClient({ section }: { section: string }) {
  const SectionContent = SECTION_COMPONENTS[section as AgentSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
