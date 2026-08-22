import type { Metadata } from "next";
import { SITE_URL } from "@/utils/config";

export const metadata: Metadata = {
  title: "Plans – Community & Pro",
  description:
    "Autopilot Monitor plans: Community is the full product and free — live session monitoring, rules engine, fleet analytics, and AI integration. Pro adds extended retention, higher limits, MSP delegation, and priority support.",
  keywords: [
    "Autopilot Monitor plans",
    "Autopilot Monitor pricing",
    "Autopilot Monitor free",
    "Autopilot Monitor Pro",
    "Windows Autopilot monitoring free",
    "Autopilot monitoring MSP",
  ],
  openGraph: {
    title: "Plans – Autopilot Monitor",
    description:
      "Community is the full product and free — and stays free. Pro adds extended retention, higher limits, MSP delegation, and priority support.",
    url: `${SITE_URL}/plans`,
  },
  twitter: {
    title: "Plans – Autopilot Monitor",
    description:
      "Community is the full product and free — and stays free. Pro adds extended retention, higher limits, MSP delegation, and priority support.",
  },
  alternates: {
    canonical: `${SITE_URL}/plans`,
  },
};

export default function PlansLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
