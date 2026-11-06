import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { authenticatedFetch } from "../authenticatedFetch";
import { viewAsHeaders } from "../viewAs";

vi.mock("../appInsights", () => ({ trackEvent: vi.fn() }));
vi.mock("../viewAs", () => ({ viewAsHeaders: vi.fn(() => ({})) }));

const TENANT = "2b3c4d5e-0000-4000-8000-00000000abcd";

function response(status: number): Response {
  return new Response("{}", { status, headers: { "Content-Type": "application/json" } });
}

const fetchMock = () => globalThis.fetch as ReturnType<typeof vi.fn>;

describe("authenticatedFetch in a view-as tab", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.mocked(viewAsHeaders).mockReturnValue({});
  });

  it("names the viewed tenant on the call and on its 401 retry, and bypasses the shared HTTP cache", async () => {
    vi.mocked(viewAsHeaders).mockReturnValue({ "X-View-As-Tenant": TENANT, "X-View-As-Role": "Viewer" });
    fetchMock().mockResolvedValueOnce(response(401)).mockResolvedValueOnce(response(200));
    const getToken = vi.fn().mockResolvedValueOnce("stale").mockResolvedValueOnce("fresh");

    await authenticatedFetch("https://api/x", getToken, { method: "POST", body: "{}" });

    for (const [, init] of fetchMock().mock.calls) {
      const headers = init.headers as Headers;
      expect(headers.get("X-View-As-Tenant")).toBe(TENANT);
      expect(headers.get("X-View-As-Role")).toBe("Viewer");
      expect(init.cache).toBe("no-store");
    }
    expect(fetchMock()).toHaveBeenCalledTimes(2);
  });

  it("adds nothing on an ordinary tab", async () => {
    fetchMock().mockResolvedValueOnce(response(200));

    await authenticatedFetch("https://api/x", vi.fn().mockResolvedValue("tok"), { cache: "reload" });

    const [, init] = fetchMock().mock.calls[0];
    expect((init.headers as Headers).has("X-View-As-Tenant")).toBe(false);
    expect(init.cache).toBe("reload");
  });
});
