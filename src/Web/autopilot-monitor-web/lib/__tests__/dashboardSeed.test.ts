import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { seedDashboardFirstFetch } from "../dashboardSeed";
import { registerSeed } from "../prefetchSeeds";
import { readViewAs } from "../viewAs";

vi.mock("../prefetchSeeds", () => ({ registerSeed: vi.fn() }));
vi.mock("../viewAs", () => ({ readViewAs: vi.fn(() => null) }));

/** Minimal browser shims — vitest runs in node (no jsdom in this suite). */
function stubDashboardTab(globalAdminMode = "false") {
  vi.stubGlobal("window", {
    location: { pathname: "/dashboard" },
    localStorage: { getItem: (k: string) => (k === "globalAdminMode" ? globalAdminMode : null) },
  });
  vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {})));
}

describe("seedDashboardFirstFetch", () => {
  beforeEach(() => {
    vi.mocked(registerSeed).mockClear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.mocked(readViewAs).mockReturnValue(null);
  });

  it("starts the dashboard's own-tenant list and stats next to auth/me", () => {
    stubDashboardTab();

    seedDashboardFirstFetch("tok", "0a1b2c3d-0000-4000-8000-00000000abcd");

    expect(registerSeed).toHaveBeenCalledTimes(2);
  });

  it("seeds nothing in a view-as tab: a seed is matched by URL alone and would show the caller's own sessions", () => {
    stubDashboardTab();
    vi.mocked(readViewAs).mockReturnValue({ tenantId: "2b3c4d5e-0000-4000-8000-00000000abcd", role: "Admin" });

    seedDashboardFirstFetch("tok", "0a1b2c3d-0000-4000-8000-00000000abcd");

    expect(registerSeed).not.toHaveBeenCalled();
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it("seeds nothing while the Global-Admin view is on", () => {
    stubDashboardTab("true");

    seedDashboardFirstFetch("tok", "0a1b2c3d-0000-4000-8000-00000000abcd");

    expect(registerSeed).not.toHaveBeenCalled();
  });
});
