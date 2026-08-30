"use client";

import { notFound } from "next/navigation";
import { type MetricsSectionId } from "../metricsNavSections";
import { SectionAgentMetrics } from "../sections/SectionAgentMetrics";
import { SectionPlatformUsage } from "../sections/SectionPlatformUsage";
import { SectionMcpUsage } from "../sections/SectionMcpUsage";
import { SectionVerdictCalibration } from "../sections/SectionVerdictCalibration";
import { SectionImePatternHealth } from "../sections/SectionImePatternHealth";

const SECTION_COMPONENTS: Record<MetricsSectionId, React.ComponentType> = {
  "platform-metrics": SectionAgentMetrics,
  "usage": SectionPlatformUsage,
  "mcp-usage": SectionMcpUsage,
  "verdict-calibration": SectionVerdictCalibration,
  "ime-pattern-health": SectionImePatternHealth,
};

export function SectionClient({ section }: { section: string }) {
  const SectionContent = SECTION_COMPONENTS[section as MetricsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
