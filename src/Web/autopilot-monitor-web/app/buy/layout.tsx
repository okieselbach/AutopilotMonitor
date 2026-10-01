import type { Metadata } from "next";
import { SITE_URL } from "@/utils/config";
import { OPEN_GRAPH_BASE, TWITTER_BASE } from "@/lib/siteMetadata";

export const metadata: Metadata = {
  title: "Get Pro – Purchase Options",
  description:
    "How to purchase Autopilot Monitor Pro: through Microsoft Marketplace or online via Cleverbridge — or try it first with a free 30-day trial. Community stays free.",
  keywords: [
    "Autopilot Monitor Pro",
    "Autopilot Monitor buy",
    "Autopilot Monitor purchase",
    "Autopilot Monitor marketplace",
    "Windows Autopilot monitoring Pro",
  ],
  openGraph: {
    ...OPEN_GRAPH_BASE,
    title: "Get Pro – Autopilot Monitor",
    description:
      "How to purchase Autopilot Monitor Pro: through Microsoft Marketplace or online via Cleverbridge.",
    url: `${SITE_URL}/buy`,
  },
  twitter: {
    ...TWITTER_BASE,
    title: "Get Pro – Autopilot Monitor",
    description:
      "How to purchase Autopilot Monitor Pro: through Microsoft Marketplace or online via Cleverbridge.",
  },
  alternates: {
    canonical: `${SITE_URL}/buy`,
  },
};

export default function BuyLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
