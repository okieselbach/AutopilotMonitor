import type { Metadata } from "next";
import { DocsSidebar } from "./DocsSidebar";

export const metadata: Metadata = {
  title: {
    template: "%s | AutopilotMonitor Docs",
    default: "Documentation | AutopilotMonitor",
  },
  description:
    "Complete setup and configuration guide for Autopilot Monitor. Deploy the bootstrapper via Intune, configure the agent, and monitor enrollments in real time.",
  keywords: [
    "Autopilot Monitor documentation",
    "Autopilot Monitor setup guide",
    "Intune bootstrapper deployment",
    "Autopilot agent configuration",
    "Windows Autopilot setup",
    "Autopilot Monitor install",
  ],
  openGraph: {
    title: "Documentation | AutopilotMonitor",
    description:
      "Complete setup and configuration guide for Autopilot Monitor. Deploy the bootstrapper via Intune and start monitoring Windows Autopilot enrollments.",
    url: "https://www.autopilotmonitor.com/docs",
  },
  twitter: {
    title: "Documentation | AutopilotMonitor",
    description:
      "Complete setup and configuration guide for Autopilot Monitor. Deploy the bootstrapper via Intune and start monitoring Windows Autopilot enrollments.",
  },
  alternates: {
    canonical: "https://www.autopilotmonitor.com/docs",
  },
};

export default function DocsLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-gray-50">
      <DocsSidebar>{children}</DocsSidebar>
    </div>
  );
}
