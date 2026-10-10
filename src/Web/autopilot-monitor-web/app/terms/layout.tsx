import type { Metadata } from "next";
import { SITE_URL } from "@/lib/config";
import { OPEN_GRAPH_BASE } from "@/lib/siteMetadata";

export const metadata: Metadata = {
  title: "Terms of Use",
  description:
    "Terms of Use for Autopilot Monitor. Read the usage terms, conditions, and acceptable use policies for the Windows Autopilot monitoring and troubleshooting platform.",
  openGraph: {
    ...OPEN_GRAPH_BASE,
    title: "Terms of Use | Autopilot Monitor",
    description: "Terms of Use for Autopilot Monitor. Read the usage terms, conditions, and acceptable use policies for the monitoring platform.",
    url: `${SITE_URL}/terms`,
  },
  alternates: {
    canonical: `${SITE_URL}/terms`,
  },
  robots: {
    index: true,
    follow: false,
  },
};

export default function TermsLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
