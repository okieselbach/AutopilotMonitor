# Autopilot Monitor – Architecture Guide

> High-level architecture overview and solution layout for contributors.
> Full product documentation lives at https://docs.autopilotmonitor.com.

---

## High-Level Overview

```text
+----------------------------------------------------------------------------------+
|                         Autopilot Monitor - Architecture                         |
+----------------------------------------------------------------------------------+

 [Admin/User Browser]
          |
          | HTTPS
          v
+-----------------------------+      OIDC        +-------------------------------+
| Web App (Next.js)           |<---------------->| Microsoft Identity (Entra ID) |
| Dashboard / Progress        |                  | Auth / Authorization          |
+-----------------------------+                  +-------------------------------+
          ^    ^
          |    |
          |    | Live updates
          |    |
          |    |                         +---------------------------+
          |    +-------------------------| SignalR Hub               |
          |                              | Real-time event stream    |
          |                              +---------------------------+
          |                                          ^
          | REST / API                               |
          v                                          | events
+---------------------------+        Data &          |
| Azure Functions Backend   |------> Integrations ---+
| APIs / Ingestion / Rules  |               \
+---------------------------+                \       +---------------------------+
          ^                                   +----->| Storage Tables            |
          | mTLS                                     | Sessions / Events / Rules |
          | telemetry / events                       +---------------------------+
          |                                          +---------------------------+
+---------------------------+                        | Notifications             |
| Agent on Windows Devices  |                        | Teams / Slack / Webhooks  |
| Enrollment + app progress |                        +---------------------------+
+---------------------------+
                                                     +---------------------------+
                                                     | MCP Server                |
                                                     | Azure Container App       |
                                                     +---------------------------+
```

**Core Data Flow:**
```
Device (Agent) ──JSON telemetry+gzip──► Azure Functions ──► Azure Table Storage
                                      │                     │
                                      ├── SignalR ─────────►│
                                      │                     │
                              Web Dashboard ◄── REST API ◄──┘
```

| Component | Technology | Purpose |
|-----------|------------|---------|
| **Agent** | .NET Framework 4.8 (Windows) | Runs on devices during enrollment, collects telemetry |
| **SummaryDialog** | .NET Framework 4.8 (WPF) | Post-enrollment summary UI (outcome, app timeline, auto-close) |
| **Backend** | Azure Functions .NET 8 (Isolated Worker) | REST API, event processing, rule engine, storage |
| **Web** | Next.js 15 + TypeScript + React 18 | Dashboard, settings, analytics, real-time UI |
| **Shared** | .NET Standard 2.0 | DTOs, models, enums, constants shared between Agent & Backend |
| **MCP Server** | Azure Container App | Model Context Protocol server for AI-assisted analysis |

---

## Solution Structure

```
AutopilotMonitor.sln
├── src/
│   ├── Shared/
│   │   ├── AutopilotMonitor.Shared/             (netstandard2.0 – DTOs, models, enums, constants)
│   │   └── AutopilotMonitor.DecisionCore/       (netstandard2.0 – completion decision engine)
│   ├── Agent/
│   │   ├── AutopilotMonitor.Agent.V2/           (net48, Exe – entry point; output named AutopilotMonitor.Agent.exe)
│   │   ├── AutopilotMonitor.Agent.V2.Core/      (net48, Lib – core logic)
│   │   ├── AutopilotMonitor.Agent.V2.Core.Tests/ (net48, xUnit – agent unit tests)
│   │   ├── AutopilotMonitor.DecisionCore.Tests/ (net48, xUnit – decision engine tests)
│   │   ├── AutopilotMonitor.SummaryDialog/      (net48, WPF – enrollment summary UI)
│   │   └── AutopilotMonitor.SummaryDialog.Tests/ (net48, xUnit)
│   ├── Backend/
│   │   ├── AutopilotMonitor.Functions/          (net8.0, Azure Functions v4)
│   │   └── AutopilotMonitor.Functions.Tests/    (net8.0, xUnit)
│   ├── McpServer/
│   │   └── autopilot-monitor-mcp/               (TypeScript/Node – MCP server; deployed as Azure Container App)
│   └── Web/
│       └── autopilot-monitor-web/               (Next.js 15, TypeScript)
├── rules/
│   ├── gather/                                   Individual gather rule JSONs
│   ├── analyze/                                  Individual analyze rule JSONs
│   ├── ime-log-patterns/                         IME regex pattern JSONs
│   ├── schema/                                   JSON Schema definitions
│   ├── guardrails.json                           Gather-rule guardrail allow-lists (source)
│   ├── scripts/                                  combine.js
│   └── dist/                                     Combined output (embedded in Functions)
├── infra/                                        Bicep templates (MCP server)
├── scripts/
│   ├── Backup/                                   Table backup/restore tooling
│   ├── Bootstrap/                                Intune deployment scripts
│   ├── CustomerSetup/                            Customer onboarding helpers
│   └── Deployment/                               CI/CD build scripts
└── .github/workflows/                            CI/CD pipelines
```

**Project References:**
```
Shared + DecisionCore ◄── Agent.V2.Core ◄── Agent.V2
                                          ◄── Agent.V2.Core.Tests
Shared + DecisionCore ◄── DecisionCore.Tests
Shared + DecisionCore ◄── Functions ◄── Functions.Tests
SummaryDialog ◄── SummaryDialog.Tests   (standalone; compiled alongside the agent build)
Web (independent – communicates via REST + SignalR)
McpServer (independent – talks to the backend REST API)
```

---
