import type { Metadata } from "next";
import { PushNav } from "./PushNav";

/**
 * The push receiver: an installable, sign-in-free app under /push/ (K24). It stays inside the
 * root layout — MSAL initialises but has no account and stays inert, and Navbar, GlobalSidebar
 * and FeedbackBubble stand down for this prefix. The manifest path is deliberately relative:
 * the receiver is installed from the portal host and its scope must stay on that origin.
 */
export const metadata: Metadata = {
  title: { absolute: "Autopilot Monitor Alerts" },
  description: "Alerts from Autopilot Monitor on this device.",
  manifest: "/push/manifest.webmanifest",
  appleWebApp: { capable: true, title: "AM Alerts", statusBarStyle: "default" },
  icons: { apple: "/push/icon-180.png", icon: "/push/icon-192.png" },
  robots: { index: false, follow: false },
};

export default function PushLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-gray-50">
      <PushNav />
      <main className="max-w-md mx-auto px-4 py-4 text-gray-900">{children}</main>
    </div>
  );
}
