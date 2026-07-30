"use client";

import { notFound } from "next/navigation";
import { type SecuritySectionId } from "../securityNavSections";
import { SectionDeviceBlock } from "../sections/SectionDeviceBlock";
import { SectionVersionBlock } from "../sections/SectionVersionBlock";
import { SectionVulnerabilityData } from "../sections/SectionVulnerabilityData";

const SECTION_COMPONENTS: Record<SecuritySectionId, React.ComponentType> = {
  "device-block": SectionDeviceBlock,
  "version-block": SectionVersionBlock,
  "vulnerability-data": SectionVulnerabilityData,
};

export function SectionClient({ section }: { section: string }) {
  const SectionContent = SECTION_COMPONENTS[section as SecuritySectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
