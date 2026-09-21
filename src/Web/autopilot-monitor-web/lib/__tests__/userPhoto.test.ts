import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { loadUserPhoto, __resetUserPhotoForTests } from "../userPhoto";

function memoryStorage(initial: Record<string, string> = {}) {
  const items = new Map(Object.entries(initial));
  return {
    items,
    getItem: (k: string) => items.get(k) ?? null,
    setItem: (k: string, v: string) => { items.set(k, v); },
  };
}

function photoResponse(): Response {
  return new Response(new Uint8Array([0xff, 0xd8, 0xff]), {
    status: 200,
    headers: { "Content-Type": "image/jpeg" },
  });
}

describe("loadUserPhoto", () => {
  beforeEach(() => {
    __resetUserPhotoForTests();
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("fetches the photo with the Graph token and returns it as a data URL", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValue(photoResponse());
    const storage = memoryStorage();

    const url = await loadUserPhoto("acc-1", async () => "graph-token", storage);

    expect(url).toBe("data:image/jpeg;base64,/9j/");
    const [requestUrl, init] = fetchMock.mock.calls[0];
    expect(requestUrl).toBe("https://graph.microsoft.com/v1.0/me/photos/64x64/$value");
    expect(init.headers.Authorization).toBe("Bearer graph-token");
    expect(storage.items.get("apm.userPhoto.acc-1")).toBe(url);
  });

  it("shares one request between concurrent callers and later mounts", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValue(photoResponse());
    const storage = memoryStorage();

    const [a, b] = await Promise.all([
      loadUserPhoto("acc-1", async () => "t", storage),
      loadUserPhoto("acc-1", async () => "t", storage),
    ]);
    await loadUserPhoto("acc-1", async () => "t", storage);

    expect(a).toBe(b);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("remembers a 404 so an account without a photo is not asked again after a reload", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValue(new Response(null, { status: 404 }));
    const storage = memoryStorage();

    expect(await loadUserPhoto("acc-1", async () => "t", storage)).toBeNull();
    __resetUserPhotoForTests(); // page reload: module state gone, sessionStorage kept
    expect(await loadUserPhoto("acc-1", async () => "t", storage)).toBeNull();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("serves a reload from sessionStorage without a token or a request", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const acquire = vi.fn();
    const storage = memoryStorage({ "apm.userPhoto.acc-1": "data:image/jpeg;base64,AAAA" });

    expect(await loadUserPhoto("acc-1", acquire, storage)).toBe("data:image/jpeg;base64,AAAA");
    expect(acquire).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("ignores a stored value that is not an image data URL", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValue(photoResponse());
    const storage = memoryStorage({ "apm.userPhoto.acc-1": "javascript:alert(1)" });

    expect(await loadUserPhoto("acc-1", async () => "t", storage)).toBe("data:image/jpeg;base64,/9j/");
  });

  it("resolves to null when the token cannot be acquired silently, and retries on the next load", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValue(photoResponse());
    const storage = memoryStorage();

    const failed = await loadUserPhoto("acc-1", async () => { throw new Error("interaction_required"); }, storage);
    expect(failed).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(storage.items.size).toBe(0);

    expect(await loadUserPhoto("acc-1", async () => "t", storage)).not.toBeNull();
  });

  it("does not render a non-image answer", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValue(new Response("<html>", { status: 200, headers: { "Content-Type": "text/html" } }));
    const storage = memoryStorage();

    expect(await loadUserPhoto("acc-1", async () => "t", storage)).toBeNull();
    expect(storage.items.size).toBe(0);
  });

  it("keeps accounts apart", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockImplementation(async () => photoResponse());
    const storage = memoryStorage();

    await loadUserPhoto("acc-1", async () => "t1", storage);
    await loadUserPhoto("acc-2", async () => "t2", storage);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect([...storage.items.keys()]).toEqual(["apm.userPhoto.acc-1", "apm.userPhoto.acc-2"]);
  });
});
