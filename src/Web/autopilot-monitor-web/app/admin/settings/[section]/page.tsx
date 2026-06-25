"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type SettingsSectionId } from "../settingsNavSections";
import { SectionGlobalSettings } from "../sections/SectionGlobalSettings";
import { SectionDiagnosticsLogPaths } from "../sections/SectionDiagnosticsLogPaths";
import { SectionMcpUsers } from "../sections/SectionMcpUsers";
import { SectionDelegatedAdmins } from "../sections/SectionDelegatedAdmins";
import { SectionConfigReseed } from "../sections/SectionConfigReseed";
import { SectionUsagePlans } from "../sections/SectionUsagePlans";
import { SectionAlerts } from "../sections/SectionAlerts";

const SECTION_COMPONENTS: Record<SettingsSectionId, React.ComponentType> = {
  "global": SectionGlobalSettings,
  "diagnostics-log-paths": SectionDiagnosticsLogPaths,
  "mcp-users": SectionMcpUsers,
  "delegated-admins": SectionDelegatedAdmins,
  "config-reseed": SectionConfigReseed,
  "usage-plans": SectionUsagePlans,
  "alerts": SectionAlerts,
};

export default function SettingsSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as SettingsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
