"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type TenantSectionId } from "../tenantNavSections";
import { SectionPlan } from "../sections/SectionPlan";
import { SectionAutopilotValidation } from "../sections/SectionAutopilotValidation";
import { SectionHardwareWhitelist } from "../sections/SectionHardwareWhitelist";
import { SectionNotifications } from "../sections/SectionNotifications";
import { SectionAccessManagement } from "../sections/SectionAccessManagement";
import { SectionBootstrapSessions } from "../sections/SectionBootstrapSessions";
import { SectionSlaTargets } from "../sections/SectionSlaTargets";
import { SectionSubmitLogs } from "../sections/SectionSubmitLogs";
import { SectionOptionalGraphCapabilities } from "../sections/SectionOptionalGraphCapabilities";

const SECTION_COMPONENTS: Record<TenantSectionId, React.ComponentType> = {
  "plan": SectionPlan,
  "autopilot": SectionAutopilotValidation,
  "hardware-whitelist": SectionHardwareWhitelist,
  "notifications": SectionNotifications,
  "sla-targets": SectionSlaTargets,
  "access-management": SectionAccessManagement,
  "bootstrap-sessions": SectionBootstrapSessions,
  "graph-permissions": SectionOptionalGraphCapabilities,
  "support": SectionSubmitLogs,
};

export default function TenantSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as TenantSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
