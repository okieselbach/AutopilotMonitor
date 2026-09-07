import { describe, expect, it } from "vitest";
import type { HealthCheck } from "@/utils/wire-types.generated";
import {
  isUrlDetail,
  visibleHealthChecks,
  visibleHealthDetails,
  resolveMcpCardState,
  MCP_WARMING_MAX_ATTEMPTS,
} from "../healthCheckView";

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

describe("resolveMcpCardState", () => {
  const warming = (message = "MCP server did not answer within 3s — it scales to zero when idle."): HealthCheck => ({
    name: "MCP Server",
    description: "AI query interface availability",
    status: "warming",
    message,
  });

  it("shows a checking card and rates nothing while the first probe is in flight", () => {
    const s = resolveMcpCardState({ check: null, loading: true, attempts: 0 });
    expect(s.display.status).toBe("checking");
    expect(s.ratedStatus).toBeNull();
    expect(s.shouldPoll).toBe(false);
  });

  it("stays neutral and not-yet-checked before anything ran", () => {
    const s = resolveMcpCardState({ check: null, loading: false, attempts: 0 });
    expect(s.display.status).toBe("unknown");
    expect(s.ratedStatus).toBeNull();
    expect(s.shouldPoll).toBe(false);
  });

  it("polls on a warming server and keeps it out of the banner", () => {
    const s = resolveMcpCardState({ check: warming(), loading: false, attempts: 0 });
    expect(s.display.status).toBe("warming");
    expect(s.ratedStatus).toBeNull();
    expect(s.shouldPoll).toBe(true);
    expect(s.display.message).toContain("attempt 1 of 8");
  });

  it("keeps showing warming while the next probe runs, so the card cannot flicker", () => {
    const s = resolveMcpCardState({ check: warming(), loading: true, attempts: 3 });
    expect(s.display.status).toBe("warming");
    expect(s.display.message).toContain("attempt 4 of 8");
    expect(s.shouldPoll).toBe(true);
  });

  it("turns a never-arriving container into a real warning once the re-polls are spent", () => {
    const s = resolveMcpCardState({ check: warming(), loading: false, attempts: MCP_WARMING_MAX_ATTEMPTS });
    expect(s.display.status).toBe("warning");
    expect(s.ratedStatus).toBe("warning");
    expect(s.shouldPoll).toBe(false);
  });

  it("rates a healthy server and stops polling", () => {
    const check: HealthCheck = { name: "MCP Server", description: "d", status: "healthy", message: "MCP server reachable (41ms)" };
    const s = resolveMcpCardState({ check, loading: false, attempts: 2 });
    expect(s.display).toBe(check);
    expect(s.ratedStatus).toBe("healthy");
    expect(s.shouldPoll).toBe(false);
  });

  it("keeps a not-permitted (unknown) card out of the banner", () => {
    const check: HealthCheck = { name: "MCP Server", description: "d", status: "unknown", message: "Not permitted" };
    const s = resolveMcpCardState({ check, loading: false, attempts: 0 });
    expect(s.ratedStatus).toBeNull();
    expect(s.shouldPoll).toBe(false);
  });

  it("rates an unhealthy server into the banner", () => {
    const check: HealthCheck = { name: "MCP Server", description: "d", status: "unhealthy", message: "MCP server returned 503" };
    const s = resolveMcpCardState({ check, loading: false, attempts: 0 });
    expect(s.ratedStatus).toBe("unhealthy");
    expect(s.shouldPoll).toBe(false);
  });
});
