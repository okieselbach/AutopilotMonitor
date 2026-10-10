import type { Metadata } from "next";
import { SITE_URL } from "@/lib/config";
import { OPEN_GRAPH_BASE } from "@/lib/siteMetadata";

export const metadata: Metadata = {
  title: "Privacy Policy",
  description:
    "Privacy Policy for Autopilot Monitor. Learn how we collect, store, and protect your data when using the Windows Autopilot monitoring platform.",
  openGraph: {
    ...OPEN_GRAPH_BASE,
    title: "Privacy Policy | Autopilot Monitor",
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
