# <img src="docs/images/logo.svg" height="28" alt="" /> AutopilotMonitor

[![Status](https://img.shields.io/badge/status-Private%20Preview-orange)](https://www.autopilotmonitor.com)
[![License](https://img.shields.io/badge/license-MIT%20%2B%20AGPL--3.0-blue)](LICENSE)
[![Website](https://img.shields.io/badge/website-autopilotmonitor.com-2ea44f)](https://www.autopilotmonitor.com)

Advanced monitoring and troubleshooting solution for Windows Autopilot deployments. Gain full visibility into every enrollment session with a detailed event timeline, fleet health dashboards, and session reporting. Define custom analysis rules to automatically detect issues and gather rules to collect targeted evidence. Retrieve diagnostics packages on demand, configure agent settings like auto-reboot behavior and automatic timezone adjustment — all managed centrally from the web dashboard.

## Private Preview

Autopilot Monitor is currently running as a **Private Preview**. Visit **[autopilotmonitor.com](https://www.autopilotmonitor.com)** to request access and learn more.

<p align="center">
  <img src="docs/images/SessionList.png" width="45%" />
  <img src="docs/images/FleetHealth.png" width="45%" />
</p>
<p align="center">
  <img src="docs/images/SessionDetails.png" width="45%" />
  <img src="docs/images/SessionTimeline.png" width="45%" />
</p>

## Overview

Autopilot Monitor provides real-time tracking, intelligent diagnostics, and automated troubleshooting for Windows Autopilot enrollment processes. It consists of:

- **Bootstrap Script** — PowerShell script deployed via Intune that starts monitoring early in the enrollment process
- **Monitoring Agent** — Lightweight .NET application that collects telemetry and evidence during enrollment
- **Backend API** — Azure Functions-based ingestion and processing pipeline
- **Web Dashboard** — Next.js application for real-time monitoring and fleet analytics

## Architecture

For detailed information about the system architecture, components, and data flow, see [Architecture Documentation](docs/architecture.md).

## Documentation

Full admin documentation is available at **[docs.autopilotmonitor.com](https://docs.autopilotmonitor.com)**

## License

This project uses a **split licensing model**:

- **MIT License** — Agent (`src/Agent/`) and Shared library (`src/Shared/`) — unrestricted use on end-user devices
- **AGPL-3.0** — Backend (`src/Backend/`), Web Dashboard (`src/Web/`), and MCP Server (`src/McpServer/`) — server-side components remain open source

See [LICENSE](LICENSE) for full details.
