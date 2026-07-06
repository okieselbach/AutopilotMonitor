import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Changelog",
  description:
    "Latest updates, improvements, and fixes for Autopilot Monitor. Stay up to date with new features and enhancements for Windows Autopilot monitoring.",
  openGraph: {
    title: "Changelog | AutopilotMonitor",
    description:
      "Latest updates, improvements, and fixes for Autopilot Monitor. Stay up to date with new features and enhancements.",
    url: "https://www.autopilotmonitor.com/changelog",
  },
  twitter: {
    title: "Changelog | AutopilotMonitor",
    description:
      "Latest updates, improvements, and fixes for Autopilot Monitor. Stay up to date with new features and enhancements.",
  },
  alternates: {
    canonical: "https://www.autopilotmonitor.com/changelog",
  },
};

export default function ChangelogLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
