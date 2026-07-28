import { REPORTING_NAV_SECTIONS } from "../reportingNavSections";
import { SectionClient } from "./SectionClient";

// Static export: prerender exactly the registered sections; anything else is a
// build-time 404 (dynamicParams=false). The interactive body lives in
// SectionClient — generateStaticParams cannot be exported from a client file.
export const dynamicParams = false;

export function generateStaticParams() {
  return REPORTING_NAV_SECTIONS.map((s) => ({ section: s.id }));
}

export default async function Page({
  params,
}: {
  params: Promise<{ section: string }>;
}) {
  const { section } = await params;
  return <SectionClient section={section} />;
}
