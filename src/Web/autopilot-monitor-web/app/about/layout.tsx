import type { Metadata } from "next";
import { SITE_URL } from "@/utils/config";

export const metadata: Metadata = {
  title: "About – Real-Time Windows Autopilot Monitoring",
  description:
    "Autopilot Monitor is a free, open-source real-time monitoring and troubleshooting platform for Windows Autopilot enrollments managed via Microsoft Intune. Built by Oliver Kieselbach.",
  keywords: [
    "Windows Autopilot monitoring",
    "Windows Autopilot troubleshooting",
    "Microsoft Intune enrollment monitoring",
    "Autopilot enrollment failures",
    "real-time Autopilot monitoring tool",
    "Enrollment Status Page monitoring",
    "ESP monitoring",
    "Autopilot diagnostics tool",
    "Windows Autopilot analytics",
    "Intune deployment monitoring",
    "open source Autopilot tool",
    "Windows device enrollment tracking",
    "Autopilot fleet health dashboard",
    "Oliver Kieselbach",
    "Autopilot analyze rules",
    "Windows Autopilot agent",
    "Intune Autopilot platform",
    "Autopilot Monitor overview",
  ],
  openGraph: {
    title: "About AutopilotMonitor – Real-Time Windows Autopilot Monitoring",
    description:
      "Free, open-source real-time monitoring and troubleshooting for Windows Autopilot enrollments. Track every phase, run analyze rules, and resolve issues faster — built by Oliver Kieselbach.",
    url: `${SITE_URL}/about`,
  },
  twitter: {
    title: "About AutopilotMonitor – Real-Time Windows Autopilot Monitoring",
    description:
      "Free, open-source real-time monitoring and troubleshooting for Windows Autopilot enrollments. Track every phase, run analyze rules, and resolve issues faster.",
  },
  alternates: {
    canonical: `${SITE_URL}/about`,
  },
};

export default function AboutLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
