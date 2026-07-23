import type { Metadata } from "next";
import { SITE_URL } from "@/utils/config";

export const metadata: Metadata = {
  title: "Terms of Use",
  description:
    "Terms of Use for Autopilot Monitor. Read the usage terms, conditions, and acceptable use policies for the Windows Autopilot monitoring and troubleshooting platform.",
  openGraph: {
    title: "Terms of Use | AutopilotMonitor",
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
