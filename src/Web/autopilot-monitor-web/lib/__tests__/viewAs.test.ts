import { afterEach, describe, expect, it, vi } from "vitest";

const TENANT = "2b3c4d5e-0000-4000-8000-00000000abcd";

/** Minimal browser shims — vitest runs in node (no jsdom in this suite). One map per tab. */
function stubWindow(search = "", stored = new Map<string, string>()) {
  const assign = vi.fn();
  const reload = vi.fn();
  vi.stubGlobal("window", {
    sessionStorage: {
      getItem: (k: string) => stored.get(k) ?? null,
      setItem: (k: string, v: string) => void stored.set(k, v),
      removeItem: (k: string) => void stored.delete(k),
    },
    location: { search, pathname: "/settings", assign, reload },
  });
  return { stored, assign, reload };
}

/** A fresh module per page load: the selection is cached for the life of the page. */
async function pageLoad() {
  vi.resetModules();
  return import("../viewAs");
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("view as tenant", () => {
  it("captures ?viewAs= into the tab, defaults the role to Admin and names it on every call", async () => {
    stubWindow(`?viewAs=${TENANT.toUpperCase()}`);
    const viewAs = await pageLoad();

    viewAs.captureViewAsFromUrl();

    expect(viewAs.readViewAs()).toEqual({ tenantId: TENANT, role: "Admin" });
    expect(viewAs.viewAsHeaders()).toEqual({ "X-View-As-Tenant": TENANT, "X-View-As-Role": "Admin" });
  });

  it("outlives the sign-in redirect of a new tab, which comes back without the parameter", async () => {
    const { stored } = stubWindow(`?viewAs=${TENANT}&viewAsRole=viewer`);
    (await pageLoad()).captureViewAsFromUrl();

    stubWindow("", stored);
    const afterRedirect = await pageLoad();
    afterRedirect.captureViewAsFromUrl();

    expect(afterRedirect.readViewAs()).toEqual({ tenantId: TENANT, role: "Viewer" });
  });

  it.each([
    ["no parameter", "?tenant=x"],
    ["not a tenant id", "?viewAs=not-a-guid"],
    ["not a tenant role", `?viewAs=${TENANT}&viewAsRole=GlobalAdmin`],
  ])("leaves an ordinary tab alone (%s)", async (_case, search) => {
    stubWindow(search);
    const viewAs = await pageLoad();

    viewAs.captureViewAsFromUrl();

    expect(viewAs.readViewAs()).toBeNull();
    expect(viewAs.viewAsHeaders()).toEqual({});
  });

  it("ignores a tampered stored selection", async () => {
    stubWindow("", new Map([["am_view_as", JSON.stringify({ tenantId: "x", role: "Admin" })]]));
    expect((await pageLoad()).readViewAs()).toBeNull();
  });

  it("removes only its own parameters from the address bar", async () => {
    stubWindow();
    const { stripViewAsParams } = await pageLoad();

    expect(stripViewAsParams(`/dashboard?viewAs=${TENANT}&viewAsRole=Viewer&days=7#top`)).toBe("/dashboard?days=7#top");
    expect(stripViewAsParams("/dashboard?days=7")).toBeNull();
  });

  it("exit turns the tab into an ordinary one on the same page", async () => {
    const { stored, assign } = stubWindow(`?viewAs=${TENANT}`);
    const viewAs = await pageLoad();
    viewAs.captureViewAsFromUrl();

    viewAs.exitViewAs();

    expect(viewAs.readViewAs()).toBeNull();
    expect(stored.size).toBe(0);
    expect(assign).toHaveBeenCalledWith("/settings");
  });

  it("a refused view-as reloads as an ordinary tab and says why exactly once", async () => {
    const { reload } = stubWindow(`?viewAs=${TENANT}`);
    const viewAs = await pageLoad();
    viewAs.captureViewAsFromUrl();

    viewAs.abandonViewAs("Viewing a tenant as its member is reserved for Global Admins.");

    expect(reload).toHaveBeenCalledTimes(1);
    expect(viewAs.readViewAs()).toBeNull();
    expect(viewAs.takeViewAsNotice()).toBe("Viewing a tenant as its member is reserved for Global Admins.");
    expect(viewAs.takeViewAsNotice()).toBeNull();
  });

  it("is what the tenant list opens", async () => {
    stubWindow();
    vi.resetModules();
    const { viewAsUrl } = await import("../routes");

    expect(viewAsUrl(TENANT)).toBe(`/dashboard?viewAs=${TENANT}`);
    expect(viewAsUrl(TENANT, "Viewer")).toBe(`/dashboard?viewAs=${TENANT}&viewAsRole=Viewer`);
  });
});
