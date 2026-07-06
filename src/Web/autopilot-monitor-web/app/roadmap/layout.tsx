import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Roadmap",
  description:
    "See what's coming next for Autopilot Monitor. Explore planned features including MSP multi-tenant support and upcoming improvements for Windows Autopilot monitoring.",
  keywords: [
    "Autopilot Monitor roadmap",
    "Autopilot Monitor upcoming features",
    "MSP Autopilot monitoring",
    "Windows Autopilot tool roadmap",
    "Autopilot Monitor future",
  ],
  openGraph: {
    title: "Roadmap | AutopilotMonitor",
    description:
      "See what's coming next for Autopilot Monitor. Explore planned features including MSP multi-tenant support and upcoming improvements.",
    url: "https://www.autopilotmonitor.com/roadmap",
  },
  twitter: {
    title: "Roadmap | AutopilotMonitor",
    description:
      "See what's coming next for Autopilot Monitor. Explore planned features including MSP multi-tenant support and upcoming improvements.",
  },
  alternates: {
    canonical: "https://www.autopilotmonitor.com/roadmap",
  },
};

export default function RoadmapLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
