"use client";

import { notFound } from "next/navigation";
import { type TenantsSectionId } from "../tenantsNavSections";
import { SectionTenantManagement } from "../sections/SectionTenantManagement";
import { SectionTenantConfigReport } from "../sections/SectionTenantConfigReport";

const SECTION_COMPONENTS: Record<TenantsSectionId, React.ComponentType> = {
  "management": SectionTenantManagement,
  "config-report": SectionTenantConfigReport,
};

export function SectionClient({ section }: { section: string }) {
  const SectionContent = SECTION_COMPONENTS[section as TenantsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
