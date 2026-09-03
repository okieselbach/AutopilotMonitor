# <img src=".github/assets/logo.svg" height="28" alt="" /> Autopilot Monitor

[![Status](https://img.shields.io/badge/status-live-brightgreen)](https://www.autopilotmonitor.com)
[![License](https://img.shields.io/badge/license-MIT%20%2B%20AGPL--3.0-blue)](LICENSE)
[![Website](https://img.shields.io/badge/website-autopilotmonitor.com-2ea44f)](https://www.autopilotmonitor.com)

Advanced monitoring and troubleshooting solution for Windows Autopilot deployments. Gain full visibility into every enrollment session with a detailed event timeline, fleet health dashboards, and session reporting. Define custom analysis rules to automatically detect issues and gather rules to collect targeted evidence. Retrieve diagnostics packages on demand, configure agent settings like auto-reboot behavior and automatic timezone adjustment — all managed centrally from the web dashboard.

## Availability

Autopilot Monitor is **publicly available** — the Community plan is free. Sign in with your work account at **[autopilotmonitor.com](https://www.autopilotmonitor.com)**; new organizations complete a short activation step after first sign-in.

<p align="center">
  <img src=".github/assets/SessionList.png" width="45%" />
  <img src=".github/assets/FleetHealth.png" width="45%" />
</p>
<p align="center">
  <img src=".github/assets/SessionDetails.png" width="45%" />
  <img src=".github/assets/SessionTimeline.png" width="45%" />
</p>

## Overview

Autopilot Monitor provides real-time tracking, intelligent diagnostics, and automated troubleshooting for Windows Autopilot enrollment processes. It consists of:

- **Bootstrap Script** — PowerShell script deployed via Intune that starts monitoring early in the enrollment process
- **Monitoring Agent** — Lightweight .NET application that collects telemetry and evidence during enrollment
- **Backend API** — Azure Functions-based ingestion and processing pipeline
- **Web Dashboard** — Next.js application for real-time monitoring and fleet analytics
- **MCP Server** — Model Context Protocol server that lets AI assistants query and troubleshoot enrollments in natural language

## AI Integration (MCP)

Autopilot Monitor ships a hosted **[Model Context Protocol](https://modelcontextprotocol.io)** server. Connect Claude Desktop, VS Code, or any MCP client that speaks Streamable HTTP with OAuth, and ask questions like:

- *"Show me all failed enrollments from the last 24 hours and group them by failure reason."*
- *"Why did session X fail, and which app install caused the delay?"*
- *"Which devices in my fleet are affected by CVE-2024-30078?"*

Server URL: `https://mcp.autopilotmonitor.com/mcp` — sign-in runs through your existing work account, access is scoped to your tenant exactly like in the portal, and no credentials are stored. Setup guide: **[AI Integration (MCP)](https://docs.autopilotmonitor.com/integrations/ai-integration-mcp)**.

## Documentation

Full admin documentation is available at **[docs.autopilotmonitor.com](https://docs.autopilotmonitor.com)**

## Feedback

Bug reports and feature requests go through [Issues](https://github.com/okieselbach/AutopilotMonitor/issues/new/choose), questions and ideas through [Discussions](https://github.com/okieselbach/AutopilotMonitor/discussions). Security issues are reported privately, see [SECURITY.md](SECURITY.md). For a problem in your own tenant, the **Report Session** button in the portal is the fastest path.

## License

This project uses a **split licensing model**. The root [LICENSE](LICENSE) is AGPL-3.0, which covers the server-side components; the device-side components carry their own MIT license file:

- **MIT License** — Agent ([`src/Agent/`](src/Agent/LICENSE)) and Shared library ([`src/Shared/`](src/Shared/LICENSE)) — unrestricted use on end-user devices, no copyleft obligations
- **AGPL-3.0** — Backend ([`src/Backend/`](src/Backend/LICENSE)), Web Dashboard ([`src/Web/`](src/Web/LICENSE)), and MCP Server ([`src/McpServer/`](src/McpServer/LICENSE)) — modifications to server-side components remain open source, especially when deployed as a network service

The Shared library is MIT because it is a dependency of the MIT-licensed Agent. The license file inside each component directory is authoritative for that component.
