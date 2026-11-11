import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  extractScriptRefsFromEvents,
  fetchScriptDisplayNames,
  lookupScriptDisplayName,
  lookUpWithOneFollowUp,
  mergeFollowUpNames,
  formatRefKey,
  scriptNamesLookupKey,
  SCRIPT_DISPLAY_NAMES_CHUNK_SIZE,
  type DisplayNamesByRefKey,
  type ScriptNamesAnswer,
} from "../scriptDisplayNames";

function mockResponse(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("extractScriptRefsFromEvents", () => {
  it("returns empty array for non-script events", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "agent_started", data: { foo: "bar" } },
      { eventType: "download_progress", data: {} },
    ]);
    expect(refs).toEqual([]);
  });

  it("collects distinct policyIds with default scriptType=platform", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "script_started", data: { policyId: "abc" } },
      { eventType: "script_completed", data: { policyId: "abc" } }, // dedup
      { eventType: "script_started", data: { policyId: "def" } },
    ]);
    expect(refs).toEqual([
      { kind: "Platform", id: "abc" },
      { kind: "Platform", id: "def" },
    ]);
  });

  it("respects scriptType to pick the right ScriptKind", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "script_completed", data: { policyId: "rem-1", scriptType: "remediation" } },
      { eventType: "script_started", data: { policy_id: "plat-2", script_type: "platform" } },
    ]);
    expect(refs).toContainEqual({ kind: "Remediation", id: "rem-1" });
    expect(refs).toContainEqual({ kind: "Platform", id: "plat-2" });
  });

  it("skips events without a policyId", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "script_started", data: { foo: "bar" } },
      { eventType: "script_completed", data: null },
      { eventType: "script_failed", data: { policyId: "" } },
    ]);
    expect(refs).toEqual([]);
  });

  it("treats Platform:{id} and Remediation:{id} as distinct refs for the same id", () => {
    const refs = extractScriptRefsFromEvents([
      { eventType: "script_started", data: { policyId: "shared", scriptType: "platform" } },
      { eventType: "script_completed", data: { policyId: "shared", scriptType: "remediation" } },
    ]);
    expect(refs).toHaveLength(2);
  });
});

describe("fetchScriptDisplayNames (POST shape)", () => {
  const TENANT = "11111111-1111-1111-1111-111111111111";

  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("returns empty when no refs are passed (no HTTP call)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const { names: result } = await fetchScriptDisplayNames(TENANT, [], getToken);
    expect(result).toEqual({});
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it("POSTs the refs in the JSON body and returns the refs map", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(200, {
      refs: { "Platform:abc": "Bootstrap.ps1", "Remediation:rem-1": null },
    }));

    const { names: result } = await fetchScriptDisplayNames(
      TENANT,
      [{ kind: "Platform", id: "abc" }, { kind: "Remediation", id: "rem-1" }],
      getToken,
    );

    expect(result).toEqual({ "Platform:abc": "Bootstrap.ps1", "Remediation:rem-1": null });

    const call = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    // URL is the bare endpoint (no refs= query string).
    expect(call[0]).toBe(`http://localhost:7071/api/tenants/${TENANT}/scripts/display-names`);
    // POST + JSON body with the refs array.
    expect(call[1].method).toBe("POST");
    const headers = call[1].headers as Headers;
    expect(headers.get("Content-Type")).toBe("application/json");
    const body = JSON.parse(call[1].body as string);
    expect(body.refs).toEqual(["Platform:abc", "Remediation:rem-1"]);
  });

  it("returns empty dict on non-2xx (transient failure)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(500, { error: "boom" }));

    const { names: result } = await fetchScriptDisplayNames(
      TENANT,
      [{ kind: "Platform", id: "abc" }],
      getToken,
    );

    expect(result).toEqual({});
  });

  it("chunks refs above the backend cap and merges the responses", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");

    // 201 refs -> two POSTs (200 + 1).
    const refs = Array.from({ length: SCRIPT_DISPLAY_NAMES_CHUNK_SIZE + 1 }, (_, i) => ({
      kind: "Platform" as const,
      id: `id-${i}`,
    }));

    const firstChunkResponse: Record<string, string> = {};
    for (let i = 0; i < SCRIPT_DISPLAY_NAMES_CHUNK_SIZE; i++) {
      firstChunkResponse[`Platform:id-${i}`] = `name-${i}`;
    }
    const secondChunkResponse = { "Platform:id-200": "name-200" };

    (globalThis.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockResponse(200, { refs: firstChunkResponse }))
      .mockResolvedValueOnce(mockResponse(200, { refs: secondChunkResponse }));

    const { names: result } = await fetchScriptDisplayNames(TENANT, refs, getToken);

    expect(globalThis.fetch).toHaveBeenCalledTimes(2);
    expect(Object.keys(result)).toHaveLength(SCRIPT_DISPLAY_NAMES_CHUNK_SIZE + 1);
    expect(result["Platform:id-0"]).toBe("name-0");
    expect(result["Platform:id-200"]).toBe("name-200");

    // Verify chunk boundaries: first POST body has 200 refs, second has 1.
    const firstBody = JSON.parse((globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0][1].body as string);
    const secondBody = JSON.parse((globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[1][1].body as string);
    expect(firstBody.refs).toHaveLength(SCRIPT_DISPLAY_NAMES_CHUNK_SIZE);
    expect(secondBody.refs).toEqual(["Platform:id-200"]);
  });

  it("partial success: keeps results from earlier chunks when a later one fails", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");

    const refs = Array.from({ length: SCRIPT_DISPLAY_NAMES_CHUNK_SIZE + 1 }, (_, i) => ({
      kind: "Platform" as const,
      id: `id-${i}`,
    }));

    (globalThis.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockResponse(200, { refs: { "Platform:id-0": "first-batch-ok" } }))
      .mockResolvedValueOnce(mockResponse(503, { error: "throttled" }));

    const { names: result } = await fetchScriptDisplayNames(TENANT, refs, getToken);

    // Don't discard the work the first chunk already did.
    expect(result["Platform:id-0"]).toBe("first-batch-ok");
    // Anything from the failed chunk is just absent (caller falls back to ID).
    expect(result["Platform:id-200"]).toBeUndefined();
  });

  it("partial success: preserves earlier chunks when a later one THROWS (network/abort)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");

    const refs = Array.from({ length: SCRIPT_DISPLAY_NAMES_CHUNK_SIZE + 1 }, (_, i) => ({
      kind: "Platform" as const,
      id: `id-${i}`,
    }));

    (globalThis.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockResponse(200, { refs: { "Platform:id-0": "first-batch-ok" } }))
      // Second chunk: simulate a network abort / fetch rejection.
      .mockRejectedValueOnce(new TypeError("Failed to fetch"));

    // Must not throw — the merged-so-far map should come back, with the failed chunk's
    // ids simply absent. Earlier behaviour bubbled the network error to the hook, which
    // then discarded the successful first chunk too.
    const { names: result } = await fetchScriptDisplayNames(TENANT, refs, getToken);

    expect(result["Platform:id-0"]).toBe("first-batch-ok");
    expect(result["Platform:id-200"]).toBeUndefined();
  });

  it("propagates TokenExpiredError from any chunk so the hook can react to auth expiry", async () => {
    const { TokenExpiredError } = await import("../authenticatedFetch");
    const getToken = vi.fn().mockResolvedValue(null); // null token -> authenticatedFetch throws

    const refs = [{ kind: "Platform" as const, id: "id-0" }];

    await expect(fetchScriptDisplayNames(TENANT, refs, getToken)).rejects.toBeInstanceOf(TokenExpiredError);
  });

  it("passes on the backend's timedOut flag and reports false when it is absent", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const refs = [{ kind: "Platform" as const, id: "abc" }];
    (globalThis.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockResponse(200, { refs: { "Platform:abc": null }, timedOut: true }))
      .mockResolvedValueOnce(mockResponse(200, { refs: { "Platform:abc": "Bootstrap.ps1" } }));

    const cut = await fetchScriptDisplayNames(TENANT, refs, getToken);
    const complete = await fetchScriptDisplayNames(TENANT, refs, getToken);

    expect(cut.timedOut).toBe(true);
    expect(complete.timedOut).toBe(false);
  });

  it("labels only the follow-up request with retry", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const refs = [{ kind: "Platform" as const, id: "abc" }];
    (globalThis.fetch as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockResponse(200, { refs: {} }))
      .mockResolvedValueOnce(mockResponse(200, { refs: {} }));

    await fetchScriptDisplayNames(TENANT, refs, getToken);
    await fetchScriptDisplayNames(TENANT, refs, getToken, true);

    const bodies = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.map(c => JSON.parse(c[1].body as string));
    expect(bodies[0]).toEqual({ refs: ["Platform:abc"] });
    expect(bodies[1]).toEqual({ refs: ["Platform:abc"], retry: true });
  });
});

describe("mergeFollowUpNames", () => {
  it("adds what the follow-up resolved and keeps what only the first answer had", () => {
    const first = { "Platform:a": "A", "Platform:b": null, "Remediation:c": "C" };
    const followUp = { "Platform:a": "A", "Platform:b": "B", "Remediation:c": null };

    expect(mergeFollowUpNames(first, followUp)).toEqual({
      "Platform:a": "A",
      "Platform:b": "B",
      // The follow-up was cut short at another point and lost this name; it stays.
      "Remediation:c": "C",
    });
  });

  it("keeps every name when the follow-up comes back empty", () => {
    const first = { "Platform:a": "A", "Platform:b": null };
    expect(mergeFollowUpNames(first, {})).toEqual(first);
  });
});

describe("lookUpWithOneFollowUp", () => {
  const answer = (names: DisplayNamesByRefKey, timedOut: boolean): ScriptNamesAnswer => ({ names, timedOut });

  /** `cancelDuring` makes the question stale while that step is in flight (a session switch, say). */
  function run(answers: ScriptNamesAnswer[], cancelDuring?: "firstAsk" | "wait" | "followUp") {
    const asks: boolean[] = [];
    const published: DisplayNamesByRefKey[] = [];
    let waits = 0;
    let cancelled = false;
    const done = lookUpWithOneFollowUp(
      async isFollowUp => {
        asks.push(isFollowUp);
        if (cancelDuring === (isFollowUp ? "followUp" : "firstAsk")) cancelled = true;
        return answers[asks.length - 1];
      },
      names => { published.push(names); },
      async () => {
        waits++;
        if (cancelDuring === "wait") cancelled = true;
      },
      () => cancelled,
    );
    return { done, asks, published, waits: () => waits };
  }

  it("asks once when the answer is complete", async () => {
    const r = run([answer({ "Platform:a": "A" }, false)]);
    await r.done;

    expect(r.asks).toEqual([false]);
    expect(r.waits()).toBe(0);
    expect(r.published).toEqual([{ "Platform:a": "A" }]);
  });

  it("waits and asks exactly once more when a time limit cut the answer short", async () => {
    const r = run([
      answer({ "Platform:a": "A", "Platform:b": null }, true),
      answer({ "Platform:a": "A", "Platform:b": "B" }, false),
    ]);
    await r.done;

    expect(r.asks).toEqual([false, true]);
    expect(r.waits()).toBe(1);
    // The first names are on screen while the follow-up runs, then the full set replaces them.
    expect(r.published).toEqual([
      { "Platform:a": "A", "Platform:b": null },
      { "Platform:a": "A", "Platform:b": "B" },
    ]);
  });

  it("stops after one follow-up even when that is cut short too", async () => {
    const r = run([
      answer({ "Platform:a": "A", "Platform:b": null }, true),
      answer({ "Platform:a": null, "Platform:b": null }, true),
      answer({ "Platform:a": "A", "Platform:b": "B" }, false),
    ]);
    await r.done;

    expect(r.asks).toEqual([false, true]);
    expect(r.waits()).toBe(1);
    // The follow-up knew less than the first answer: nothing already shown is taken away.
    expect(r.published[1]).toEqual({ "Platform:a": "A", "Platform:b": null });
  });

  it("publishes nothing and sends no follow-up once the question is no longer current", async () => {
    const answers = [answer({ "Platform:a": null }, true), answer({ "Platform:a": "A" }, false)];

    const duringFirstAsk = run(answers, "firstAsk");
    await duringFirstAsk.done;
    expect(duringFirstAsk.published).toEqual([]);
    expect(duringFirstAsk.asks).toEqual([false]);

    const duringWait = run(answers, "wait");
    await duringWait.done;
    expect(duringWait.asks).toEqual([false]); // the follow-up is never sent
    expect(duringWait.published).toEqual([{ "Platform:a": null }]);

    const duringFollowUp = run(answers, "followUp");
    await duringFollowUp.done;
    expect(duringFollowUp.asks).toEqual([false, true]);
    expect(duringFollowUp.published).toEqual([{ "Platform:a": null }]); // its answer is dropped
  });
});

describe("lookupScriptDisplayName", () => {
  it("returns null for unknown ref", () => {
    const map = { "Platform:abc": "Found" };
    expect(lookupScriptDisplayName(map, "platform", "def")).toBeNull();
  });

  it("distinguishes Platform vs Remediation under the same id", () => {
    const map = {
      "Platform:shared": "Platform Name",
      "Remediation:shared": "Remediation Name",
    };
    expect(lookupScriptDisplayName(map, "platform", "shared")).toBe("Platform Name");
    expect(lookupScriptDisplayName(map, "remediation", "shared")).toBe("Remediation Name");
  });

  it("treats unknown scriptType as Platform (safer default for legacy events)", () => {
    const map = { "Platform:abc": "X" };
    expect(lookupScriptDisplayName(map, undefined, "abc")).toBe("X");
    expect(lookupScriptDisplayName(map, "", "abc")).toBe("X");
  });

  it("returns null when map or policyId missing", () => {
    expect(lookupScriptDisplayName(undefined, "platform", "abc")).toBeNull();
    expect(lookupScriptDisplayName({}, "platform", undefined)).toBeNull();
  });
});

describe("scriptNamesLookupKey", () => {
  const TENANT_ID = "11111111-1111-1111-1111-111111111111";
  const REFS_KEY = "Platform:abc|Remediation:def";

  it("is empty until the caller's event list is complete", () => {
    // An empty key keeps the hook from fetching: the session page loads events page by page,
    // and a lookup per page would repeat the request for every page that adds a script.
    expect(scriptNamesLookupKey(TENANT_ID, REFS_KEY, false)).toBe("");
  });

  it("joins tenant and refset once the caller is ready", () => {
    expect(scriptNamesLookupKey(TENANT_ID, REFS_KEY, true)).toBe(`${TENANT_ID}::${REFS_KEY}`);
  });

  it("is empty without a tenant or without script refs", () => {
    expect(scriptNamesLookupKey(null, REFS_KEY, true)).toBe("");
    expect(scriptNamesLookupKey(undefined, REFS_KEY, true)).toBe("");
    expect(scriptNamesLookupKey("", REFS_KEY, true)).toBe("");
    expect(scriptNamesLookupKey(TENANT_ID, "", true)).toBe("");
  });

  it("changes with the tenant and with the refset, so an answer never serves another question", () => {
    const key = scriptNamesLookupKey(TENANT_ID, REFS_KEY, true);
    expect(scriptNamesLookupKey("22222222-2222-2222-2222-222222222222", REFS_KEY, true)).not.toBe(key);
    expect(scriptNamesLookupKey(TENANT_ID, "Platform:abc", true)).not.toBe(key);
  });
});

describe("formatRefKey", () => {
  it("formats canonical Kind:Id", () => {
    expect(formatRefKey({ kind: "Platform", id: "abc" })).toBe("Platform:abc");
    expect(formatRefKey({ kind: "Remediation", id: "rem-1" })).toBe("Remediation:rem-1");
  });

  it("produces a stable, sortable refset fingerprint from any ordering", () => {
    // The hook's internal refsKey is built by `.map(formatRefKey).sort().join("|")`.
    // Asserting the building blocks here keeps the hook's stable-key contract honest
    // without needing a full React renderer.
    const a = [{ kind: "Platform" as const, id: "two" }, { kind: "Platform" as const, id: "one" }];
    const b = [{ kind: "Platform" as const, id: "one" }, { kind: "Platform" as const, id: "two" }];
    const fingerprint = (refs: typeof a) => refs.map(formatRefKey).sort().join("|");
    expect(fingerprint(a)).toBe(fingerprint(b));
    expect(fingerprint(a)).toBe("Platform:one|Platform:two");
  });
});
