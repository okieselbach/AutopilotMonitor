import { describe, expect, it } from "vitest";
import type { HealthCheck } from "@/utils/wire-types.generated";
import { isUrlDetail, visibleHealthChecks, visibleHealthDetails } from "../healthCheckView";

const checks: HealthCheck[] = [
  {
    name: "Storage Backend",
    description: "Data storage connectivity",
    status: "healthy",
    message: "Storage reachable (0ms)",
    details: { "Table endpoint": "https://example.table.core.windows.net" },
  },
  {
    name: "Agent Binaries",
    description: "Agent download package availability",
    status: "healthy",
    message: "Agent package and bootstrap script available (48ms)",
    details: {
      "Agent ZIP": "https://download.example.com/agent/Agent.zip",
      "Bootstrap script": "https://download.example.com/agent/Install.ps1",
      "Legacy blob (keepalive)": "https://legacy.blob.core.windows.net/agent",
    },
  },
  {
    name: "SignalR Quota",
    description: "Connection + daily message usage vs. plan limits",
    status: "healthy",
    message: "Within plan limits",
    details: { "Connections (max/1h)": "6/1000 (0%)", Resource: "/subscriptions/x" },
  },
  {
    name: "Poison Queues",
    description: "Async-worker dead-letter backlog",
    status: "healthy",
    message: "All poison queues empty",
    details: { "foo-poison": "0 messages" },
  },
  {
    name: "MCP Server",
    description: "AI query interface availability",
    status: "healthy",
    message: "MCP server reachable (6ms)",
    details: { "Server URL": "https://mcp.example.com", Version: "1.6.410" },
  },
];

describe("isUrlDetail", () => {
  it("recognises http(s) strings only", () => {
    expect(isUrlDetail("https://a.b")).toBe(true);
    expect(isUrlDetail("HTTP://a.b")).toBe(true);
    expect(isUrlDetail("1.6.410")).toBe(false);
    expect(isUrlDetail("/subscriptions/x")).toBe(false);
    expect(isUrlDetail(42)).toBe(false);
    expect(isUrlDetail(undefined)).toBe(false);
  });
});

describe("visibleHealthChecks", () => {
  it("returns the same reference in the operator view — nothing is hidden", () => {
    expect(visibleHealthChecks(checks, true)).toBe(checks);
  });

  it("drops SignalR Quota and Poison Queues outside the operator view, like the server does", () => {
    const names = visibleHealthChecks(checks, false).map((c) => c.name);
    expect(names).toEqual(["Storage Backend", "Agent Binaries", "MCP Server"]);
  });

  it("strips endpoint URLs from the remaining cards but keeps non-URL rows", () => {
    const visible = visibleHealthChecks(checks, false);
    expect(visible.find((c) => c.name === "Storage Backend")?.details).toBeUndefined();
    expect(visible.find((c) => c.name === "Agent Binaries")?.details).toBeUndefined();
    expect(visible.find((c) => c.name === "MCP Server")?.details).toEqual({ Version: "1.6.410" });
  });

  it("does not mutate the input", () => {
    const before = JSON.stringify(checks);
    visibleHealthChecks(checks, false);
    expect(JSON.stringify(checks)).toBe(before);
  });
});

describe("visibleHealthDetails", () => {
  it("passes undefined through and leaves the operator view untouched", () => {
    expect(visibleHealthDetails(undefined, false)).toBeUndefined();
    const d = { "Server URL": "https://mcp.example.com" };
    expect(visibleHealthDetails(d, true)).toBe(d);
  });

  it("returns undefined rather than an empty object when every row was a URL", () => {
    expect(visibleHealthDetails({ "Server URL": "https://mcp.example.com" }, false)).toBeUndefined();
  });
});
