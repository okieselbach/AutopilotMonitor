import type { Metadata } from "next";
import { SITE_URL } from "@/utils/config";
import { OPEN_GRAPH_BASE, TWITTER_BASE } from "@/lib/siteMetadata";

export const metadata: Metadata = {
  title: "Help & Support – How to Get Help",
  description:
    "How to get help with Autopilot Monitor: open a GitHub issue for bugs and feature requests, or reach out directly to Oliver Kieselbach on LinkedIn. Plus documentation, FAQ, and service announcements.",
  keywords: [
    "Autopilot Monitor support",
    "Autopilot Monitor help",
    "Windows Autopilot monitoring support",
    "Autopilot Monitor GitHub issues",
    "Autopilot Monitor contact",
    "Oliver Kieselbach",
  ],
  openGraph: {
    ...OPEN_GRAPH_BASE,
    title: "Help & Support – Autopilot Monitor",
    description:
      "How to get help with Autopilot Monitor: open a GitHub issue or reach out directly on LinkedIn.",
    url: `${SITE_URL}/help`,
  },
  twitter: {
    ...TWITTER_BASE,
    title: "Help & Support – Autopilot Monitor",
    description:
      "How to get help with Autopilot Monitor: open a GitHub issue or reach out directly on LinkedIn.",
  },
  alternates: {
    canonical: `${SITE_URL}/help`,
  },
};

export default function HelpLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
