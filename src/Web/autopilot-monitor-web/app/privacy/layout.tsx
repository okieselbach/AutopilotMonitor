import type { Metadata } from "next";
import { SITE_URL } from "@/utils/config";

export const metadata: Metadata = {
  title: "Privacy Policy",
  description:
    "Privacy Policy for Autopilot Monitor. Learn how we collect, store, and protect your data when using the Windows Autopilot monitoring platform.",
  openGraph: {
    title: "Privacy Policy | AutopilotMonitor",
    description:
      "Privacy Policy for Autopilot Monitor. Learn how we collect, store, and protect your data.",
    url: `${SITE_URL}/privacy`,
  },
  alternates: {
    canonical: `${SITE_URL}/privacy`,
  },
  robots: {
    index: true,
    follow: false,
  },
};

export default function PrivacyLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
