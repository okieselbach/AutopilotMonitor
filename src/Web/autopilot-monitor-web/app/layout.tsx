import type { Metadata } from "next";
import { Inter } from "next/font/google";
import "./globals.css";
import { AuthProvider } from "../contexts/AuthContext";
import { SignalRProvider } from "../contexts/SignalRContext";
import { TenantProvider } from "../contexts/TenantContext";
import { NotificationProvider } from "../contexts/NotificationContext";
import { GlobalNotificationProvider } from "../contexts/GlobalNotificationContext";
import { TenantNotificationProvider } from "../contexts/TenantNotificationContext";
import { ThemeProvider } from "../contexts/ThemeContext";
import Navbar from "../components/Navbar";
import ScrollToTopButton from "../components/ScrollToTopButton";
import FeedbackBubble from "../components/FeedbackBubble";
import { SidebarProvider } from "../contexts/SidebarContext";
import { GlobalSidebar } from "../components/GlobalSidebar";
import AppInsightsInit from "../components/AppInsightsInit";
import ChunkReloadRecovery from "../components/ChunkReloadRecovery";
import { HostRoutingGuard } from "../components/HostRoutingGuard";
import { LegacyPathRedirect } from "../components/LegacyPathRedirect";
import { DOCS_URL, SITE_URL } from "@/utils/config";

const inter = Inter({ subsets: ["latin"] });

export const metadata: Metadata = {
  metadataBase: new URL(SITE_URL),
  title: {
    absolute: "Autopilot Monitor – Real-Time Windows Enrollment Monitoring",
    default: "Autopilot Monitor",
    template: "%s | Autopilot Monitor",
  },
  description:
    "Real-time monitoring and troubleshooting for Windows deployments. Track every enrollment phase, detect issues automatically with Analyze Rules, and resolve failures faster.",
  keywords: [
    "Windows Autopilot monitoring",
    "Autopilot deployment visibility",
    "Intune Autopilot analytics",
    "Windows enrollment tracking",
    "Autopilot troubleshooting",
    "Autopilot real-time monitoring",
    "Autopilot Monitor",
    "Windows Autopilot dashboard",
    "Autopilot failure detection",
    "enrollment phase tracking",
    "Windows device enrollment",
    "OOBE monitoring",
    "Autopilot ESP",
    "Intune enrollment monitoring",
  ],
  authors: [{ name: "Oliver Kieselbach", url: "https://www.linkedin.com/in/oliver-kieselbach/" }],
  creator: "Oliver Kieselbach",
  openGraph: {
    type: "website",
    locale: "en_US",
    url: SITE_URL,
    siteName: "Autopilot Monitor",
    title: "Autopilot Monitor – Real-Time Windows Enrollment Monitoring",
    description:
      "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track every enrollment phase, detect issues automatically, and resolve failures faster.",
  },
  twitter: {
    card: "summary_large_image",
    title: "Autopilot Monitor – Real-Time Windows Enrollment Monitoring",
    description:
      "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track every enrollment phase, detect issues automatically, and resolve failures faster.",
  },
  alternates: {
    canonical: SITE_URL,
  },
  verification: {
    google: "qqIx6VoSjaNL-Idu78il6i3n76_ax9OUT44saxaGyac",
  },
  robots: {
    index: true,
    follow: true,
    googleBot: {
      index: true,
      follow: true,
    },
  },
  icons: {
    icon: "/icon.svg",
    shortcut: "/icon.svg",
    // Safari cannot use SVG for apple-touch-icon; must be an opaque PNG
    // (served by the app/apple-icon.png file convention).
    apple: "/apple-icon.png",
  },
};

// Structured data for search and AI answer engines. One graph with stable
// @ids so the entities link up: Oliver Kieselbach is the author/publisher of
// the software and the site, glueckkanja AG is the provider that operates the
// hosted service (see /about and /terms). Claims here are customer-facing
// (.claude/CLAUDE.md, "Customer-Facing Claims") — verify before editing.
const PERSON_ID = `${SITE_URL}/#oliver-kieselbach`;
const ORG_ID = `${SITE_URL}/#glueckkanja`;
const GITHUB_REPO = "https://github.com/okieselbach/AutopilotMonitor";

const jsonLd = {
  "@context": "https://schema.org",
  "@graph": [
    {
      "@type": "Person",
      "@id": PERSON_ID,
      name: "Oliver Kieselbach",
      url: "https://oliverkieselbach.com",
      jobTitle: "Microsoft MVP",
      sameAs: [
        "https://www.linkedin.com/in/oliver-kieselbach/",
        "https://github.com/okieselbach",
        "https://oliverkieselbach.com",
      ],
    },
    {
      "@type": "Organization",
      "@id": ORG_ID,
      name: "glueckkanja AG",
      url: "https://www.glueckkanja.com",
      address: { "@type": "PostalAddress", addressCountry: "DE" },
    },
    {
      "@type": "WebSite",
      "@id": `${SITE_URL}/#website`,
      url: SITE_URL,
      name: "Autopilot Monitor",
      inLanguage: "en",
      publisher: { "@id": PERSON_ID },
    },
    {
      "@type": "SoftwareApplication",
      "@id": `${SITE_URL}/#software`,
      name: "Autopilot Monitor",
      url: SITE_URL,
      description:
        "Free, open-source real-time monitoring and troubleshooting platform for Windows Autopilot enrollments managed through Microsoft Intune. A temporary agent streams enrollment events to a web portal; analyze rules detect failure patterns automatically; an MCP server lets AI assistants query the data.",
      applicationCategory: "BusinessApplication",
      applicationSubCategory: "IT monitoring",
      operatingSystem: "Web (portal), Windows (agent)",
      isAccessibleForFree: true,
      offers: {
        "@type": "Offer",
        name: "Community plan",
        price: "0",
        priceCurrency: "EUR",
        url: `${SITE_URL}/plans`,
      },
      author: { "@id": PERSON_ID },
      creator: { "@id": PERSON_ID },
      publisher: { "@id": PERSON_ID },
      provider: { "@id": ORG_ID },
      softwareHelp: { "@type": "CreativeWork", url: DOCS_URL },
      sameAs: [GITHUB_REPO],
      featureList: [
        "Real-time enrollment monitoring with live push updates",
        "Analyze rules that detect enrollment failure patterns automatically",
        "Per-session event timeline with ESP phases, app installs, errors, and performance snapshots",
        "Diagnostics collection (agent, IME, and device information bundle) without touching the device",
        "Fleet health dashboard with success rates, failure trends, and enrollment duration",
        "Delegated read-only administration across customer tenants for MSPs",
        "Model Context Protocol (MCP) server for AI assistants",
        "Notifications to Microsoft Teams, Slack, Discord, and generic JSON webhooks",
      ],
      keywords:
        "Windows Autopilot, Microsoft Intune, enrollment monitoring, Autopilot troubleshooting, Enrollment Status Page, Windows deployment",
    },
  ],
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  // data-scroll-behavior: Next 16 no longer auto-suppresses CSS smooth-scroll
  // during route navigation; without the opt-in, globals.css's
  // `scroll-behavior: smooth` would animate every route change to top.
  return (
    <html lang="en" data-scroll-behavior="smooth">
      <body className={inter.className}>
        <script
          type="application/ld+json"
          dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd).replace(/</g, "\\u003c") }}
        />
        <ThemeProvider>
          <AuthProvider>
            <SignalRProvider>
              <NotificationProvider>
                <GlobalNotificationProvider>
                  <TenantNotificationProvider>
                    <TenantProvider>
                      <SidebarProvider>
                        <AppInsightsInit />
                        <ChunkReloadRecovery />
                        <HostRoutingGuard />
                        <LegacyPathRedirect />
                        <Navbar />
                        <GlobalSidebar>
                          {children}
                        </GlobalSidebar>
                        <ScrollToTopButton />
                        <FeedbackBubble />
                      </SidebarProvider>
                    </TenantProvider>
                  </TenantNotificationProvider>
                </GlobalNotificationProvider>
              </NotificationProvider>
            </SignalRProvider>
          </AuthProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
