"use client";

import { notFound } from "next/navigation";
import { type ReportingSectionId } from "../reportingNavSections";
import { SectionMcpUsage } from "../sections/SectionMcpUsage";

const SECTION_COMPONENTS: Record<ReportingSectionId, React.ComponentType> = {
  "mcp-usage": SectionMcpUsage,
};

export function SectionClient({ section }: { section: string }) {
  const SectionContent = SECTION_COMPONENTS[section as ReportingSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
