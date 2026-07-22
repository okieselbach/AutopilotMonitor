import Link from "next/link";
import { PublicPageHeader } from "../../components/PublicPageHeader";

const FEATURES = [
  {
    title: "Real-Time Enrollment Monitoring",
    description:
      "Track every Windows Autopilot enrollment phase as it happens. Live push updates surface device registration, ESP progress, app installs, and user phase transitions without manual refreshing.",
    color: "blue",
    bullets: ["Live phase-by-phase tracking", "Near real-time push updates via SignalR", "Per-device event stream with timestamps"],
  },
  {
    title: "Intelligent Analyze Rules",
    description:
      "Built-in and fully customizable analyze rules automatically detect enrollment failure patterns — from reboot loops and app timeouts to policy conflicts and IME log anomalies.",
    color: "indigo",
    bullets: ["Community-driven built-in rules", "Custom rule authoring", "Confidence-scored findings per session"],
  },
  {
    title: "Fleet Health Dashboard",
    description:
      "A high-level view across your entire device fleet. Monitor success rates, failure trends, average enrollment duration, and blocked devices — broken down by time range.",
    color: "purple",
    bullets: ["Success & failure rate trends", "Average enrollment duration", "Blocked device detection"],
  },
  {
    title: "Diagnostics Collection",
    description:
      "Collect agent logs, IME logs, agent state, and device information as a ZIP bundle at the end of an enrollment — then download it from the session view without touching the device.",
    color: "green",
    bullets: ["Configurable upload: off, always, or on failure", "Agent, IME & device information bundle", "Configurable additional log paths"],
  },
  {
    title: "Detailed Event Timeline",
    description:
      "Full event timeline for every deployment session. Drill down into phase transitions, app install status, errors, warnings, and performance snapshots to pinpoint root causes fast.",
    color: "orange",
    bullets: ["Phase-by-phase breakdown", "App install progress & details", "Error & warning highlights"],
  },
  {
    title: "Audit Logging & Compliance",
    description:
      "Complete audit trail of all administrative actions and configuration changes. Meet compliance requirements with detailed, tenant-scoped records and configurable data retention.",
    color: "red",
    bullets: ["Admin action history", "Configurable retention policies", "Tenant-scoped audit log"],
  },
];

const colorMap: Record<string, { bg: string; text: string; dot: string }> = {
  blue:   { bg: "bg-blue-100",   text: "text-blue-600",   dot: "bg-blue-400" },
  indigo: { bg: "bg-indigo-100", text: "text-indigo-600", dot: "bg-indigo-400" },
  purple: { bg: "bg-purple-100", text: "text-purple-600", dot: "bg-purple-400" },
  green:  { bg: "bg-green-100",  text: "text-green-600",  dot: "bg-green-400" },
  orange: { bg: "bg-orange-100", text: "text-orange-600", dot: "bg-orange-400" },
  red:    { bg: "bg-red-100",    text: "text-red-600",    dot: "bg-red-400" },
};

export default function AboutPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicPageHeader title="About Autopilot Monitor" />

      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-10 space-y-10">

        {/* What is Autopilot Monitor */}
        <section className="bg-white rounded-xl shadow-sm border border-gray-100 p-8">
          <h2 className="text-2xl font-bold text-gray-900 mb-4">What is Autopilot Monitor?</h2>
          <p className="text-gray-700 leading-relaxed mb-4">
            <strong>Autopilot Monitor</strong> is a free, open-source, real-time monitoring and troubleshooting
            platform for <strong>Windows Autopilot enrollments</strong> managed through{" "}
            <strong>Microsoft Intune</strong>. It gives IT administrators, helpdesk engineers, and MSPs
            complete visibility into every enrollment session — from the first boot through the Enrollment
            Status Page (ESP) to the user desktop — so issues can be detected and resolved before they
            impact end users.
          </p>
          <p className="text-gray-700 leading-relaxed mb-4">
            Traditional Autopilot deployments are a black box. When a device fails or stalls, the only
            option is to manually dig through IME logs or wait for a user complaint. Autopilot Monitor
            changes that by streaming live telemetry from a lightweight agent deployed via Intune, feeding
            every event — app installs, policy applications, phase transitions, errors, and performance
            data — into a central dashboard with intelligent analysis built in.
          </p>
          <p className="text-gray-700 leading-relaxed">
            Deployed by assigning a bootstrapper script in Intune, the platform requires no infrastructure
            changes and no additional certificates on end user devices. It runs entirely on
            <strong> Azure</strong>, authenticates via <strong>Microsoft Entra ID</strong>, and provides
            multi-tenant support with strict per-tenant data isolation.
          </p>
        </section>

        {/* Key Features */}
        <section>
          <h2 className="text-2xl font-bold text-gray-900 mb-2">Key Features</h2>
          <p className="text-gray-600 mb-6 leading-relaxed">
            Every feature is designed around one goal: reducing the time between an Autopilot failure
            occurring and an IT admin understanding why.
          </p>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-5">
            {FEATURES.map((f) => {
              const c = colorMap[f.color];
              return (
                <div key={f.title} className="bg-white rounded-xl border border-gray-100 shadow-sm p-6">
                  <h3 className={`text-base font-semibold mb-2 ${c.text}`}>{f.title}</h3>
                  <p className="text-sm text-gray-600 leading-relaxed mb-3">{f.description}</p>
                  <ul className="space-y-1">
                    {f.bullets.map((b) => (
                      <li key={b} className="flex items-center text-xs text-gray-500">
                        <span className={`w-1.5 h-1.5 rounded-full ${c.dot} mr-2 shrink-0`} />
                        {b}
                      </li>
                    ))}
                  </ul>
                </div>
              );
            })}
          </div>
        </section>

        {/* Who It's For */}
        <section className="bg-white rounded-xl shadow-sm border border-gray-100 p-8">
          <h2 className="text-2xl font-bold text-gray-900 mb-4">Who Is It For?</h2>
          <p className="text-gray-700 leading-relaxed mb-5">
            Autopilot Monitor is built for anyone responsible for deploying or supporting
            Windows devices through Microsoft Intune and Autopilot.
          </p>
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            {[
              {
                title: "IT Administrators",
                description:
                  "Gain full visibility into Autopilot deployments across your organization. Detect failures early, analyze patterns, and reduce helpdesk tickets from day-one device issues.",
              },
              {
                title: "Helpdesk & Field Engineers",
                description:
                  "Immediately understand what happened on a specific device without touching it. Access event timelines, analyze rule findings, and download diagnostics on demand.",
              },
              {
                title: "MSPs & Enterprise Teams",
                description:
                  "Autopilot Monitor is a multi-tenant service with strict per-tenant data isolation — telemetry, configuration, and diagnostics are partitioned and access-scoped to each tenant. MSPs can be granted delegated read access across several customer tenants from a single login.",
              },
            ].map((item) => (
              <div key={item.title} className="rounded-lg bg-blue-50 border border-blue-100 p-4">
                <h3 className="text-sm font-semibold text-blue-900 mb-2">{item.title}</h3>
                <p className="text-sm text-blue-800 leading-relaxed">{item.description}</p>
              </div>
            ))}
          </div>
        </section>

        {/* How It Works */}
        <section className="bg-white rounded-xl shadow-sm border border-gray-100 p-8">
          <h2 className="text-2xl font-bold text-gray-900 mb-4">How It Works</h2>
          <p className="text-gray-700 leading-relaxed mb-5">
            Autopilot Monitor uses a lightweight .NET agent deployed to devices via an Intune bootstrapper
            script. The agent monitors the enrollment process in real time and streams telemetry events
            — including ESP phases, app installs, performance snapshots, and custom gather rule data —
            to the Azure-hosted backend pipeline. The portal displays live session data, runs analyze rules
            automatically, and alerts on failure conditions.
          </p>
          <ol className="space-y-3">
            {[
              { step: "1", text: "Assign the bootstrapper PowerShell script to your Autopilot device groups in Intune." },
              { step: "2", text: "The bootstrapper installs the Autopilot Monitor Agent on each enrolling device." },
              { step: "3", text: "The agent captures live enrollment events and uploads them to the backend pipeline." },
              { step: "4", text: "The portal displays real-time session data, analyze rule results, and fleet health metrics." },
              { step: "5", text: "On completion, the agent uploads a diagnostics bundle if configured, then removes itself." },
            ].map((item) => (
              <li key={item.step} className="flex items-start gap-3 text-sm text-gray-700">
                <span className="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-blue-600 text-white text-xs font-bold">
                  {item.step}
                </span>
                {item.text}
              </li>
            ))}
          </ol>
        </section>

        {/* Tech Stack & Platform */}
        <section className="bg-white rounded-xl shadow-sm border border-gray-100 p-8">
          <h2 className="text-2xl font-bold text-gray-900 mb-4">Technology &amp; Platform</h2>
          <p className="text-gray-700 leading-relaxed mb-5">
            Autopilot Monitor is built on modern, enterprise-grade technology designed to scale with large
            device fleets and multi-tenant deployments.
          </p>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-6">
            {[
              {
                title: "Backend",
                items: [
                  "Azure Functions (.NET 10 Isolated, Flex Consumption) — serverless, scalable API",
                  "Azure Table Storage — high-throughput event ingestion",
                  "Azure Blob Storage — diagnostics and log bundle storage",
                  "Azure SignalR Service — real-time push to the portal",
                ],
              },
              {
                title: "Portal (Web Frontend)",
                items: [
                  "Next.js 15 (React 18) + TypeScript — fast, server-rendered React app",
                  "Microsoft Entra ID (MSAL) — secure authentication",
                  "Role-based access control (Admin / Operator / Viewer)",
                  "Multi-tenant architecture with delegated MSP access",
                ],
              },
              {
                title: "Agent",
                items: [
                  ".NET binary — lightweight, low-overhead monitoring",
                  "Runs via scheduled task (no Windows service — easy, residue-free removal)",
                  "Deployed via an Intune platform script (PowerShell bootstrapper)",
                  "Mutual TLS using the existing Intune MDM device certificate",
                  "Self-destruct on enrollment completion (on by default — removes task and files)",
                ],
              },
              {
                title: "Integrations",
                items: [
                  "Microsoft Intune — agent deployment target",
                  "Microsoft Teams, Slack & generic JSON webhooks — start, success, failure, hardware rejection and SLA alerts",
                  "Intune Management Extension (IME) log — event source for log pattern detection",
                  "WMI & Registry — extended data gather rules",
                ],
              },
            ].map((group) => (
              <div key={group.title}>
                <h3 className="text-sm font-semibold text-gray-800 mb-2">{group.title}</h3>
                <ul className="space-y-1">
                  {group.items.map((item) => (
                    <li key={item} className="flex items-start text-sm text-gray-600">
                      <span className="w-1.5 h-1.5 rounded-full bg-gray-300 mt-1.5 mr-2 shrink-0" />
                      {item}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </section>

        {/* Open Source */}
        <section className="bg-white rounded-xl shadow-sm border border-gray-100 p-8">
          <h2 className="text-2xl font-bold text-gray-900 mb-4">Open Source &amp; Free to Use</h2>
          <p className="text-gray-700 leading-relaxed mb-4">
            Autopilot Monitor is fully open source and free to use. The complete source code is available on
            GitHub under an open license. Contributions, bug reports, and feature requests from the
            community are welcome — especially for Analyze Rules, which are designed to be shared and
            extended by the wider Windows Autopilot community.
          </p>
          <p className="text-gray-700 leading-relaxed mb-4">
            Autopilot Monitor was created and is maintained by{" "}
            <strong>Oliver Kieselbach</strong>, a Microsoft MVP and long-time contributor to the Windows
            Autopilot and Microsoft Intune community. The project is driven by real-world enterprise
            deployment experience and community feedback.
          </p>
          <p className="text-gray-700 leading-relaxed mb-4">
            The hosted service is operated by <strong>glueckkanja AG</strong> — see the{" "}
            <a href="https://www.glueckkanja.com/en/imprint" target="_blank" rel="noopener noreferrer" className="text-blue-600 hover:text-blue-800 underline">Imprint</a> for company
            details and the <Link href="/terms" className="text-blue-600 hover:text-blue-800 underline">Terms of Use</Link>{" "}
            for what each plan does and does not commit to.
          </p>
          <div className="flex flex-wrap gap-3 mt-5">
            <a
              href="https://github.com/okieselbach/Autopilot-Monitor"
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-gray-50 px-4 py-2.5 text-sm font-medium text-gray-700 hover:border-gray-300 hover:bg-gray-100 transition-colors"
            >
              <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
              </svg>
              View on GitHub
            </a>
            <a
              href="https://github.com/okieselbach/Autopilot-Monitor/issues"
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-gray-50 px-4 py-2.5 text-sm font-medium text-gray-700 hover:border-gray-300 hover:bg-gray-100 transition-colors"
            >
              Submit Feedback
            </a>
            <a
              href="https://www.linkedin.com/in/oliver-kieselbach/"
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-gray-50 px-4 py-2.5 text-sm font-medium text-gray-700 hover:border-gray-300 hover:bg-gray-100 transition-colors"
            >
              Oliver Kieselbach on LinkedIn
            </a>
          </div>
        </section>

        {/* Quick Links */}
        <section className="bg-white rounded-xl shadow-sm border border-gray-100 p-8">
          <h2 className="text-2xl font-bold text-gray-900 mb-4">Explore Further</h2>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-sm">
            <a href="https://docs.autopilotmonitor.com" target="_blank" rel="noopener noreferrer" className="rounded-lg border border-gray-200 px-4 py-3 hover:border-blue-300 hover:bg-blue-50/50 transition-colors text-gray-700 font-medium">
              Documentation →
            </a>
            <a href="https://docs.autopilotmonitor.com/changelog/platform-changelog" target="_blank" rel="noopener noreferrer" className="rounded-lg border border-gray-200 px-4 py-3 hover:border-blue-300 hover:bg-blue-50/50 transition-colors text-gray-700 font-medium">
              Changelog →
            </a>
            <Link href="/privacy" className="rounded-lg border border-gray-200 px-4 py-3 hover:border-blue-300 hover:bg-blue-50/50 transition-colors text-gray-700 font-medium">
              Privacy Policy →
            </Link>
          </div>
        </section>

      </main>
    </div>
  );
}
