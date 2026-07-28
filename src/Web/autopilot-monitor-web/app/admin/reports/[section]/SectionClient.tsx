"use client";

import { notFound } from "next/navigation";
import { REPORTS_NAV_SECTIONS, type ReportsSectionId } from "../reportsNavSections";
import { SectionSessionReports } from "../sections/SectionSessionReports";
import { SectionDistressReports } from "../sections/SectionDistressReports";
import { SectionUserFeedback } from "../sections/SectionUserFeedback";
import { SectionSessionExport } from "../sections/SectionSessionExport";

const SECTION_COMPONENTS: Record<ReportsSectionId, React.ComponentType> = {
  "session-reports": SectionSessionReports,
  "distress-reports": SectionDistressReports,
  "user-feedback": SectionUserFeedback,
  "session-export": SectionSessionExport,
};

export function SectionClient({ section }: { section: string }) {
  const SectionContent = SECTION_COMPONENTS[section as ReportsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
