# <img src=".github/assets/logo.svg" height="28" alt="Autopilot Monitor logo" /> Autopilot Monitor

[![Status](https://img.shields.io/badge/status-live-brightgreen)](https://www.autopilotmonitor.com)
[![License](https://img.shields.io/badge/license-MIT%20%2B%20AGPL--3.0-blue)](LICENSE)
[![Website](https://img.shields.io/badge/website-autopilotmonitor.com-2ea44f)](https://www.autopilotmonitor.com)

Autopilot Monitor is an open-source, real-time monitoring and troubleshooting platform for **Windows Autopilot** and **Microsoft Intune** enrollment. It records enrollment events, application installations, policies, device state, and diagnostics while provisioning is still running, instead of collecting logs by hand after a failure. Administrators get live enrollment tracking, Enrollment Status Page (ESP) visibility, detailed session timelines, automated issue detection, on-demand diagnostics, fleet health analytics, and AI-assisted troubleshooting through a hosted Model Context Protocol (MCP) server.

The platform supports classic Windows Autopilot and modern Intune provisioning scenarios, including **Windows Autopilot Device Preparation**.

## Availability

Autopilot Monitor is **publicly available** — the Community plan is free. Sign in with your work account at **[autopilotmonitor.com](https://www.autopilotmonitor.com)**; new organizations complete a short activation step after first sign-in.

<p align="center">
  <img src=".github/assets/SessionList.png" width="45%" alt="Autopilot Monitor dashboard showing live Windows Autopilot and Intune enrollment sessions" />
  <img src=".github/assets/FleetHealth.png" width="45%" alt="Autopilot Monitor fleet health dashboard with Windows Autopilot enrollment analytics and success metrics" />
</p>
<p align="center">
  <img src=".github/assets/SessionDetails.png" width="45%" alt="Autopilot Monitor session details showing device information, diagnostics, apps, policies, and enrollment status" />
  <img src=".github/assets/SessionTimeline.png" width="45%" alt="Windows Autopilot enrollment timeline showing ESP phases, application installs, policies, events, and errors" />
</p>

## Key Features

- **Real-time Windows Autopilot monitoring** — follow device provisioning while enrollment is still running
- **Detailed enrollment timelines** — correlate Autopilot, ESP, Intune Management Extension, apps, policies, device state, and system events
- **Enrollment Status Page visibility** — see which phase, application, or policy is delaying provisioning
- **Automated issue detection** — define custom analysis rules that identify known failure patterns
- **Targeted evidence collection** — gather registry values, event log entries, files, log-parser matches, and allow-listed commands when specific conditions are detected
- **On-demand diagnostics** — request diagnostics packages from enrolled devices remotely
- **Fleet health analytics** — success rates, deployment duration, device models, applications, errors, and recurring issues
- **Session reporting** — inspect and share detailed enrollment information for individual devices
- **Windows Autopilot Device Preparation support** — monitor Device Preparation deployments alongside classic Autopilot scenarios
- **Centralized agent configuration** — control diagnostic behavior, automatic reboots, timezone adjustment, and other monitoring settings from the web dashboard
- **AI-assisted troubleshooting** — query enrollment data through the built-in Model Context Protocol server

## How it works

Autopilot Monitor consists of several components working together throughout Windows enrollment:

- **Bootstrap Script** — PowerShell script deployed through Microsoft Intune that starts monitoring early in the provisioning process
- **Monitoring Agent** — lightweight .NET application that collects telemetry and evidence during enrollment
- **Backend API** — Azure Functions-based ingestion and processing pipeline
- **Web Dashboard** — Next.js application for real-time monitoring, troubleshooting, and fleet analytics
- **MCP Server** — Model Context Protocol server that lets AI assistants query Autopilot Monitor data in natural language

## Windows Autopilot and Intune Troubleshooting

Autopilot Monitor helps investigate common Windows provisioning and enrollment problems such as:

- Windows Autopilot deployments that fail or appear stuck
- Enrollment Status Page (ESP) delays and unusually long deployment phases
- Win32 application installation failures and slow application downloads
- Intune Management Extension problems
- Microsoft Entra join, Hybrid Join, and device registration issues
- policies that block or delay provisioning
- inconsistent behavior across hardware models and recurring failures affecting multiple devices

Because telemetry is collected during enrollment, many problems can be investigated without reproducing the issue or manually collecting logs from the affected device.

## AI Integration (MCP)

Autopilot Monitor ships a hosted **[Model Context Protocol](https://modelcontextprotocol.io)** server. Connect Claude Desktop, VS Code, or any MCP client that speaks Streamable HTTP with OAuth, and ask questions like:

- *"Show me all failed enrollments from the last 24 hours and group them by failure reason."*
- *"Why did session X fail, and which app install caused the delay?"*
- *"Which devices had unusually long ESP application phases this week?"*
- *"Which device models have the highest Autopilot failure rate?"*
- *"Which devices in my fleet are affected by CVE-2024-30078?"*

Server URL: `https://mcp.autopilotmonitor.com/mcp` — sign-in runs through your existing work account, access is scoped to your tenant exactly like in the portal, and the MCP server stores no credentials. Setup guide: **[AI Integration (MCP)](https://docs.autopilotmonitor.com/integrations/ai-integration-mcp)**.

## Documentation

Full admin documentation is available at **[docs.autopilotmonitor.com](https://docs.autopilotmonitor.com)** — deployment, configuration, monitoring, diagnostics, troubleshooting, analysis and gather rules, integrations, and AI-assisted troubleshooting.

## Open Source

Autopilot Monitor is developed openly on GitHub. The source is published so that customers and security reviewers can see what runs on their devices and with their data: the repository contains the device agent, shared library, backend, web dashboard, and MCP server.

## Feedback

Bug reports and feature requests go through [Issues](https://github.com/okieselbach/AutopilotMonitor/issues/new/choose), questions and ideas through [Discussions](https://github.com/okieselbach/AutopilotMonitor/discussions). Security issues are reported privately, see [SECURITY.md](SECURITY.md). For a problem in your own tenant, the **Report Session** button in the portal is the fastest path.

## License

This project uses a **split licensing model**. The root [LICENSE](LICENSE) is AGPL-3.0, which covers the server-side components; the device-side components carry their own MIT license file:

- **MIT License** — Agent ([`src/Agent/`](src/Agent/LICENSE)) and Shared library ([`src/Shared/`](src/Shared/LICENSE)) — unrestricted use on end-user devices, no copyleft obligations
- **AGPL-3.0** — Backend ([`src/Backend/`](src/Backend/LICENSE)), Web Dashboard ([`src/Web/`](src/Web/LICENSE)), and MCP Server ([`src/McpServer/`](src/McpServer/LICENSE)) — modifications to server-side components remain open source, especially when deployed as a network service

The Shared library is MIT because it is a dependency of the MIT-licensed Agent. The license file inside each component directory is authoritative for that component.
