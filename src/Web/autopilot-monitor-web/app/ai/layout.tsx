import type { Metadata } from "next";
import { SITE_URL } from "@/utils/config";
import { OPEN_GRAPH_BASE, TWITTER_BASE } from "@/lib/siteMetadata";

const SHARE_TITLE = "AI Analysis for Windows Autopilot – Bring Your Own AI";
const SHARE_DESCRIPTION =
  "Ask your Autopilot enrollments anything in Claude, ChatGPT, VS Code, a terminal agent or a self-hosted client. Read-only, Microsoft sign-in, included in every plan.";

export const metadata: Metadata = {
  title: "AI Analysis – Ask Your Autopilot Enrollments",
  description:
    "Connect Claude, ChatGPT, VS Code with GitHub Copilot, a terminal agent or a self-hosted client to the Autopilot Monitor MCP server and ask about your Windows Autopilot enrollments in plain language. Read-only, Microsoft sign-in, included in every plan.",
  keywords: [
    "Windows Autopilot AI analysis",
    "Autopilot MCP server",
    "Model Context Protocol Intune",
    "Intune AI assistant",
    "Claude Autopilot troubleshooting",
    "ChatGPT Intune enrollment",
    "GitHub Copilot MCP Intune",
    "Autopilot enrollment root cause AI",
    "bring your own AI",
  ],
  openGraph: {
    ...OPEN_GRAPH_BASE,
    title: SHARE_TITLE,
    description: SHARE_DESCRIPTION,
    url: `${SITE_URL}/ai`,
  },
  twitter: {
    ...TWITTER_BASE,
    title: SHARE_TITLE,
    description: SHARE_DESCRIPTION,
  },
  alternates: {
    canonical: `${SITE_URL}/ai`,
  },
};

export default function AiLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
