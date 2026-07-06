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

const inter = Inter({ subsets: ["latin"] });

export const metadata: Metadata = {
  metadataBase: new URL("https://www.autopilotmonitor.com"),
  title: {
    absolute: "AutopilotMonitor – Real-Time Windows Enrollment Monitoring",
    default: "AutopilotMonitor",
    template: "%s | AutopilotMonitor",
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
    url: "https://www.autopilotmonitor.com",
    siteName: "AutopilotMonitor",
    title: "AutopilotMonitor – Real-Time Windows Enrollment Monitoring",
    description:
      "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track every enrollment phase, detect issues automatically, and resolve failures faster.",
  },
  twitter: {
    card: "summary_large_image",
    title: "AutopilotMonitor – Real-Time Windows Enrollment Monitoring",
    description:
      "Real-time monitoring and troubleshooting for Windows Autopilot deployments. Track every enrollment phase, detect issues automatically, and resolve failures faster.",
  },
  alternates: {
    canonical: "https://www.autopilotmonitor.com",
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
    apple: "/icon.svg",
  },
};

const jsonLd = {
  "@context": "https://schema.org",
  "@type": "SoftwareApplication",
  name: "AutopilotMonitor",
  description:
    "Real-time monitoring and troubleshooting platform for Windows deployments. Gives IT teams full visibility into enrollment phases, app progress, errors, and timelines.",
  applicationCategory: "BusinessApplication",
  operatingSystem: "Web",
  offers: {
    "@type": "Offer",
    price: "0",
    priceCurrency: "USD",
  },
  author: {
    "@type": "Person",
    name: "Oliver Kieselbach",
    url: "https://www.linkedin.com/in/oliver-kieselbach/",
  },
  url: "https://www.autopilotmonitor.com",
  codeRepository: "https://github.com/okieselbach/Autopilot-Monitor",
  keywords:
    "Windows Autopilot, Intune, enrollment monitoring, autopilot troubleshooting, Windows deployment",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body className={inter.className}>
        <script
          type="application/ld+json"
          dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd) }}
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
