import { describe, expect, it } from "vitest";
import type {
  GetGlobalMcpUsageOverviewResponse,
  McpUsageAccountItem,
  McpUsageMinuteItem,
  McpUsageTenantItem,
  McpUsageToolItem,
  PlanTierDefinitionsResponse,
  TenantConfiguration,
} from "@/lib/generated/wire-types.generated";
import {
  accountNearLimit,
  dailyChart,
  formatChars,
  formatDayKey,
  formatDuration,
  formatShare,
  minuteRowsToShow,
  minuteShare,
  overviewTotals,
  parseView,
  planLegend,
  searchTenants,
  sortTenants,
  sortTools,
  tenantInfoMap,
  tenantLabel,
  tenantNearLimit,
  usagePercent,
} from "../mcpUsage/mcpUsageLogic";

const TENANT_A = "aaaaaaaa-0000-0000-0000-00000000000a";
const TENANT_B = "bbbbbbbb-0000-0000-0000-00000000000b";

function account(principal: string, over: Partial<McpUsageAccountItem> = {}): McpUsageAccountItem {
  return {
    principal,
    isPlatformAdmin: false,
    quota: { plan: "community", dailyLimit: 50, monthlyLimit: 250, dailyUsed: 10, monthlyUsed: 20 },
    calls: 5,
    requests: 10,
    quotaHits: 0,
    topTools: [],
    ...over,
  };
}

function tenant(tenantId: string, accounts: McpUsageAccountItem[], over: Partial<McpUsageTenantItem> = {}): McpUsageTenantItem {
  return {
    tenantId,
    quota: { tenantPlan: "community", dailyLimit: 100, monthlyLimit: 250, dailyUsed: 10, monthlyUsed: 20 },
    calls: accounts.reduce((s, a) => s + a.calls, 0),
    requests: accounts.reduce((s, a) => s + a.requests, 0),
    quotaHits: 0,
    accounts,
    ...over,
  };
}

function config(tenantId: string, domainName: string, planTier: string, over: Partial<TenantConfiguration> = {}): TenantConfiguration {
  return { tenantId, domainName, planTier, trialConsumed: false, payingCustomer: false, disabled: false, ...over } as TenantConfiguration;
}

const INFO = tenantInfoMap(
  [config(TENANT_A.toUpperCase(), "contoso.com", "free", { companyName: "Contoso Ltd" }), config(TENANT_B, "fabrikam.com", "pro")],
  Date.UTC(2026, 10, 5),
);

describe("parseView", () => {
  it("keeps the two other tabs and falls back to tenants", () => {
    expect(parseView("tools")).toBe("tools");
    expect(parseView("limits")).toBe("limits");
    expect(parseView(null)).toBe("tenants");
    expect(parseView("Tools")).toBe("tenants");
  });
});

describe("tenantInfoMap", () => {
  it("keys by the lower-cased id and resolves the edition", () => {
    expect(INFO.get(TENANT_A)?.domainName).toBe("contoso.com");
    expect(INFO.get(TENANT_A)?.edition).toBe("community");
    expect(INFO.get(TENANT_B)?.edition).toBe("pro");
    expect(tenantLabel(TENANT_B, INFO)).toBe("fabrikam.com");
    expect(tenantLabel("cccccccc-0000-0000-0000-00000000000c", INFO)).toBe("cccccccc…");
  });
});

describe("usagePercent and near-limit", () => {
  it("reads unlimited and unavailable windows as no share", () => {
    expect(usagePercent(40, 50)).toBe(80);
    expect(usagePercent(71, 50)).toBe(142);
    expect(usagePercent(5, 0)).toBeNull();
    expect(usagePercent(-1, 50)).toBeNull();
  });

  it("flags an account at 80 % of a window or with a refusal", () => {
    expect(accountNearLimit(account("a@contoso.com", { quota: { plan: "community", dailyLimit: 50, monthlyLimit: 250, dailyUsed: 40, monthlyUsed: 40 } }))).toBe(true);
    expect(accountNearLimit(account("a@contoso.com", { quotaHits: 1 }))).toBe(true);
    expect(accountNearLimit(account("a@contoso.com"))).toBe(false);
    expect(accountNearLimit(account("a@contoso.com", { quota: undefined }))).toBe(false);
  });

  it("flags a tenant through its organization window or any account", () => {
    expect(tenantNearLimit(tenant(TENANT_A, [account("a@contoso.com")]))).toBe(false);
    expect(tenantNearLimit(tenant(TENANT_A, [account("a@contoso.com")], { quota: { tenantPlan: "community", dailyLimit: 100, monthlyLimit: 250, dailyUsed: 10, monthlyUsed: 200 } }))).toBe(true);
    expect(tenantNearLimit(tenant(TENANT_A, [account("a@contoso.com", { quotaHits: 2 })]))).toBe(true);
  });
});

describe("sortTenants", () => {
  it("puts an unlimited window below every real share", () => {
    const limited = tenant(TENANT_A, [account("a@contoso.com")]);
    const unlimited = tenant(TENANT_B, [account("b@fabrikam.com")], { quota: { tenantPlan: "pro", dailyLimit: 0, monthlyLimit: 0, dailyUsed: 900, monthlyUsed: 900 } });
    expect(sortTenants([unlimited, limited], "today", "desc").map((t) => t.tenantId)).toEqual([TENANT_A, TENANT_B]);
    expect(sortTenants([limited, unlimited], "requests", "asc").map((t) => t.tenantId)).toEqual([TENANT_A, TENANT_B]);
  });
});

describe("searchTenants", () => {
  const alice = account("alice@contoso.com");
  const app = account("app:11111111-2222-3333-4444-555555555555");
  const bob = account("bob@fabrikam.com", { quotaHits: 1 });
  const tenants = [tenant(TENANT_A, [alice, app]), tenant(TENANT_B, [bob])];

  it("keeps every tenant and account without a query", () => {
    expect(searchTenants(tenants, INFO, "  ", false).map((m) => m.accounts.length)).toEqual([2, 1]);
  });

  it("keeps all accounts of a tenant matched by domain, company or id", () => {
    for (const q of ["CONTOSO.COM", "contoso ltd", "aaaaaaaa"]) {
      const [match] = searchTenants(tenants, INFO, q, false);
      expect(match.tenant.tenantId).toBe(TENANT_A);
      expect(match.accounts).toHaveLength(2);
      expect(match.matchedByAccount).toBe(false);
    }
  });

  it("keeps only the matching accounts of a tenant matched by an account, and opens it", () => {
    const [match] = searchTenants(tenants, INFO, "11111111-2222", false);
    expect(match.accounts.map((a) => a.principal)).toEqual([app.principal]);
    expect(match.matchedByAccount).toBe(true);
  });

  it("near a limit only keeps tenants and accounts at a limit", () => {
    const matches = searchTenants(tenants, INFO, "", true);
    expect(matches.map((m) => m.tenant.tenantId)).toEqual([TENANT_B]);
    expect(matches[0].accounts).toEqual([bob]);
  });
});

function overview(over: Partial<GetGlobalMcpUsageOverviewResponse> = {}): GetGlobalMcpUsageOverviewResponse {
  return {
    dateFrom: "20261103",
    dateTo: "20261105",
    includesPlatformAdmins: false,
    telemetry: { requestsAvailable: true, mcpLogAvailable: true },
    daily: [],
    tenants: [],
    tools: [],
    quotaHits: [],
    minuteLimits: { accounts: [] },
    ...over,
  };
}

function tool(name: string, calls: number, over: Partial<McpUsageToolItem> = {}): McpUsageToolItem {
  return { tool: name, calls, accounts: 1, tenants: 1, requestsPerCall: 1, routes: [], ...over };
}

function minute(principal: string, over: Partial<McpUsageMinuteItem> = {}): McpUsageMinuteItem {
  return { tenantId: TENANT_A, principal, peakCalls: 0, mcpRefused: 0, peakRequests: 0, backendLimit: 120, backendRefused: 0, ...over };
}

describe("overviewTotals", () => {
  it("counts MCP-only calls in the total and every limited account once", () => {
    const totals = overviewTotals(
      overview({
        tenants: [tenant(TENANT_A, [account("alice@contoso.com"), account("app:11111111-2222-3333-4444-555555555555")]), tenant(TENANT_B, [account("bob@fabrikam.com")])],
        tools: [tool("get_session_summary", 10), tool("search_docs", 4, { accounts: undefined, tenants: undefined, requestsPerCall: 0 })],
        quotaHits: [
          { date: "20261105", tenantId: TENANT_A, principal: "alice@contoso.com", level: "user", scope: "daily", plan: "community", refused: 3, firstAt: "", lastAt: "" },
          { date: "20261104", tenantId: TENANT_A, principal: "alice@contoso.com", level: "user", scope: "daily", plan: "community", refused: 1, firstAt: "", lastAt: "" },
        ],
        minuteLimits: { accounts: [minute("alice@contoso.com", { backendRefused: 2 }), minute("bob@fabrikam.com", { tenantId: TENANT_B, mcpRefused: 7 })] },
      }),
      INFO,
    );

    expect(totals.calls).toBe(14);
    expect(totals.mcpOnlyCalls).toBe(4);
    expect(totals.toolCount).toBe(2);
    expect(totals.requests).toBe(30);
    expect([totals.tenants, totals.communityTenants, totals.proTenants]).toEqual([2, 1, 1]);
    expect([totals.accounts, totals.apps]).toEqual([3, 1]);
    expect([totals.limitAccounts, totals.quotaHitAccounts, totals.minuteRefusedAccounts]).toEqual([2, 1, 2]);
  });
});

describe("dailyChart", () => {
  it("stacks the plans by their total calls and fills days without calls with 0", () => {
    const chart = dailyChart(
      overview({
        daily: [
          { date: "20261104", calls: 3, requests: 9, accounts: 1, callsByPlan: [{ plan: "pro", calls: 3 }] },
          { date: "20261105", calls: 12, requests: 20, accounts: 2, callsByPlan: [{ plan: "community", calls: 10 }, { plan: "pro", calls: 2 }] },
        ],
      }),
    );

    expect(chart.plans).toEqual(["community", "pro"]);
    expect(chart.rows[0]).toEqual({ date: "20261104", calls: 3, requests: 9, accounts: 1, "plan:community": 0, "plan:pro": 3 });
  });
});

describe("per-minute rows", () => {
  it("takes the higher share of the two limits", () => {
    expect(minuteShare(minute("a", { peakCalls: 30, peakRequests: 108 }), 60)).toBe(90);
    expect(minuteShare(minute("a", { peakCalls: 45, peakRequests: 12 }), 60)).toBe(75);
    // No MCP limit reported yet: only the backend share counts.
    expect(minuteShare(minute("a", { peakCalls: 59, peakRequests: 12 }), undefined)).toBe(10);
  });

  it("shows a refusal at any peak, else a peak at half a limit", () => {
    const rows = [
      minute("low", { peakCalls: 5, peakRequests: 10 }),
      minute("refused", { peakRequests: 10, backendRefused: 1 }),
      minute("close", { peakCalls: 30 }),
    ];
    expect(minuteRowsToShow(rows, 60).map((r) => r.principal)).toEqual(["refused", "close"]);
  });
});

describe("sortTools", () => {
  it("puts a figure the MCP log did not deliver below every real value", () => {
    const tools = [tool("a", 5, { errorRate: undefined }), tool("b", 1, { errorRate: 0.2 }), tool("c", 9, { errorRate: 0 })];
    expect(sortTools(tools, "errorRate", "desc").map((t) => t.tool)).toEqual(["b", "c", "a"]);
    expect(sortTools(tools, "calls", "desc").map((t) => t.tool)).toEqual(["c", "a", "b"]);
  });
});

describe("planLegend", () => {
  it("fills a blank organization window of community or pro from the catalog only", () => {
    const response = {
      tiers: [
        { name: "community", dailyRequestLimit: 50, monthlyRequestLimit: 250, description: "" },
        { name: "msp", dailyRequestLimit: 1500, monthlyRequestLimit: 15000, description: "" },
      ],
      catalog: [
        { edition: "community", dailyRequestLimit: 100, monthlyRequestLimit: 3000, tenantDailyRequestLimit: 300, tenantMonthlyRequestLimit: 9000, includedDelegatedSlots: 0, slotDailyRequestLimit: 0, slotMonthlyRequestLimit: 0, slotTenantDailyRequestLimit: 0, slotTenantMonthlyRequestLimit: 0 },
      ],
    } as PlanTierDefinitionsResponse;

    expect(planLegend(response)).toEqual([
      { name: "community", daily: 50, monthly: 250, tenantDaily: 300, tenantMonthly: 9000 },
      { name: "msp", daily: 1500, monthly: 15000, tenantDaily: undefined, tenantMonthly: undefined },
    ]);
    expect(planLegend(null)).toEqual([]);
  });
});

describe("formatting", () => {
  it("formats durations, sizes and shares", () => {
    expect(formatDuration(95)).toBe("95 ms");
    expect(formatDuration(812)).toBe("812 ms");
    expect(formatDuration(1029)).toBe("1.0 s");
    expect(formatDuration(undefined)).toBe("—");
    expect(formatChars(167)).toBe("167 B");
    expect(formatChars(19277)).toBe("19.3 KB");
    expect(formatShare(0)).toBe("0 %");
    expect(formatShare(0.0321)).toBe("3.2 %");
    expect(formatShare(0.25)).toBe("25 %");
    expect(formatShare(undefined)).toBe("—");
  });

  it("leaves anything but a yyyyMMdd day unchanged", () => {
    expect(formatDayKey("2026-11-05")).toBe("2026-11-05");
    expect(formatDayKey("20261105")).not.toBe("20261105");
  });
});
