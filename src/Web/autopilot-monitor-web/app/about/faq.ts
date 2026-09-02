// Single source for the "Common Questions" section on /about and the
// FAQPage JSON-LD emitted next to it, so the visible text and the structured
// data can never drift apart.
//
// Questions are phrased the way a user states their PROBLEM ("can I follow an
// enrollment live?"), not the way we name a feature; the answer is the
// feature. Every answer is a customer-facing claim (.claude/CLAUDE.md,
// "Customer-Facing Claims"): verify each statement against the docs (the
// built-in rules reference, the portal guide, plans.md) before editing —
// never carry a phrase forward because it was already on the page. Answers
// are plain text: no markup, no line breaks, one self-contained paragraph
// that reads correctly when quoted on its own.

export type AboutFaqItem = { question: string; answer: string };

export const ABOUT_FAQ: AboutFaqItem[] = [
  {
    question: "What is Autopilot Monitor?",
    answer:
      "Autopilot Monitor is a free, open-source monitoring and troubleshooting platform for Windows Autopilot enrollments managed through Microsoft Intune. A lightweight, temporary agent runs on each device during enrollment and streams events to a web portal, where IT admins watch progress live, get failures analyzed automatically, and review historical sessions and fleet-wide reports.",
  },
  {
    question: "Can I follow a Windows Autopilot enrollment live?",
    answer:
      "Yes. Assign the Autopilot Monitor bootstrapper script to your Autopilot device groups in Intune. During enrollment the agent captures Enrollment Status Page (ESP) phases, app downloads and installs, errors, reboots, and performance snapshots and pushes them to the portal as they happen. You see every device's progress without refreshing the page or touching the device.",
  },
  {
    question: "Why did my Autopilot enrollment fail, and how do I find out without touching the device?",
    answer:
      "Open the session in the portal. Analyze rules flag known failure patterns automatically: app install error codes such as 1603, detection-rule failures that break the ESP (0x87D1041C), blocking-app timeouts, content download and proxy failures, TPM attestation and MDM enrollment error codes, hybrid join problems, failed Windows updates, and low disk space or battery. Guided Diagnosis names the primary suspect with a copyable quick fix, and a diagnostics bundle with agent and IME logs can be uploaded automatically or on demand.",
  },
  {
    question: "Can failed enrollments be analyzed automatically instead of reading IME logs by hand?",
    answer:
      "Yes. Dozens of built-in, community-maintained analyze rules run automatically on every session and report confidence-scored findings with remediation steps. You can write your own rules, add Intune Management Extension (IME) log patterns, and gather registry, file, or WMI data on any event. Regression detection alerts tenant admins when a rule starts firing more often than usual.",
  },
  {
    question: "How do I get alerted when an Autopilot enrollment fails?",
    answer:
      "Configure a notification channel for Microsoft Teams, Slack, Discord, or a generic JSON webhook and choose which triggers fire: enrollment start, success, or failure. The same channel carries SLA breach and resolution alerts, consecutive-failure alerts, and hardware rejection notices. Configuration and hardware alerts also appear as bell notifications in the portal.",
  },
  {
    question: "Is there reporting on Autopilot deployments, such as success rate, duration, and failing apps?",
    answer:
      "Yes. Fleet Health shows the success rate, average enrollment time, a daily enrollments timeline, top failure reasons, the slowest and most-failing device models and apps with their exit codes, and a first-time-right rate that reveals devices that needed several attempts. SLA Compliance reports against your own targets for success rate, P95 duration, and app install success, with a list of violating sessions.",
  },
  {
    question: "Why does enrollment take so long, and which app is slowing it down?",
    answer:
      "Every finished session gets a time attribution bar that splits the enrollment into device preparation, apps, identity and Windows Hello, user ESP, and desktop handoff, and lists the apps that blocked the ESP with their install times. Fleet Health aggregates the same data per enrollment class and ranks the apps that cost the most time. Geographic Performance finds slow sites and shows how much content came from Delivery Optimization peers.",
  },
  {
    question: "Can end users or field technicians check a device's enrollment status without portal access?",
    answer:
      "Yes. The Progress Portal shows a device's enrollment status by serial number: a color-coded status, a progress bar, the enrollment steps, and what is currently downloading or installing, all updating live. No portal role is required, the serial number acts as the access key, and the view is strictly read-only with no access to timelines or device internals.",
  },
  {
    question: "Is Autopilot Monitor free and open source?",
    answer:
      "Yes. The Community plan is free and stays free, includes the complete current feature set, and is meant for production fleets, not just labs. The source code is on GitHub: the agent under the MIT license, the backend, portal, and MCP server under AGPL-3.0. A commercial Pro plan is coming for organizations that need reliability commitments, priority support, longer data retention, and delegated administration across customer tenants.",
  },
  {
    question: "How is the agent deployed, and does it stay on the device?",
    answer:
      "The agent is a small .NET binary installed by an Intune platform script (PowerShell bootstrapper); for Autopilot Device Preparation it ships as a thin MSI line-of-business app. It runs as a scheduled task, not a Windows service, authenticates with the existing Intune MDM device certificate, and exists only for the duration of the enrollment: by default it removes its task and files on completion, and it never runs longer than six hours.",
  },
  {
    question: "Which Autopilot scenarios are supported?",
    answer:
      "User-driven, pre-provisioned (white glove), and self-deploying or kiosk Autopilot flows, for Microsoft Entra joined and Hybrid joined devices alike. Autopilot Device Preparation is supported, including device association as a validation method, and Windows 365 Cloud PCs can be enabled per tenant.",
  },
  {
    question: "Where is my enrollment data stored?",
    answer:
      "In Germany. All customer data and all compute that touches it run in the Azure region Germany West Central. Only the static portal front-end is served from West Europe, and it stores no customer data. Retention is configurable per tenant with a 90-day default, diagnostics packages can be kept in your own Azure Blob Storage, and a tenant can offboard and irreversibly delete all its data at any time.",
  },
  {
    question: "Can a managed service provider monitor several customer tenants?",
    answer:
      "Yes. Delegated administration gives an MSP read-only access to a defined set of customer tenants from a single login, with fleet analytics scoped to exactly those tenants. Secrets are redacted, write operations are structurally unavailable, and every grant or revoke is written to the customer's own audit log. The managing tenant needs the Pro plan; customer tenants can be on any plan.",
  },
  {
    question: "Can I ask an AI assistant about my enrollments?",
    answer:
      "Yes. Autopilot Monitor exposes a Model Context Protocol (MCP) server. Connect Claude Desktop, VS Code with Claude, or any MCP client that supports Streamable HTTP with OAuth, and ask questions like \"show me all failed enrollments from the last 24 hours\" or \"why did this session fail?\". Access follows your portal role and is scoped to your tenant, with usage limits tied to the tenant's plan.",
  },
  {
    question: "Who builds and operates Autopilot Monitor?",
    answer:
      "Autopilot Monitor was created and is maintained by Oliver Kieselbach, a Microsoft MVP and long-time contributor to the Windows Autopilot and Microsoft Intune community. The hosted service is operated by glueckkanja AG, a German company certified to ISO/IEC 27001, which is also the contracting party for the Pro plan.",
  },
];
