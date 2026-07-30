/**
 * Client-side helpers for resolving Intune script display names from the backend.
 *
 * The backend endpoint requires the customer tenant to have granted the optional
 * `DeviceManagementScripts.Read.All` Graph application permission to the Autopilot
 * Monitor service principal (see /settings/tenant/graph-permissions and the
 * `Grant-AutopilotMonitorAddOn.ps1` script). When the permission is missing the
 * endpoint returns 200 with all entries null, so the caller never has to branch
 * on auth errors -- falling back to "show the ID" is the natural default.
 *
 * Wire shape: POST `tenants/{tenantId}/scripts/display-names` with JSON body
 * `{ refs: ["Platform:<id>", "Remediation:<id>", ...] }`. We use POST (not GET)
 * so payloads with many refs cannot bump into browser / Azure Functions URL
 * length limits.
 */
import { useEffect, useMemo, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

/** Matches the shape produced by `useAuth().getAccessToken` (may return null when expired). */
export type GetAccessToken = (forceRefresh?: boolean) => Promise<string | null>;

/** Mirrors backend `AutopilotMonitor.Shared.Models.Graph.ScriptKind`. */
export type ScriptKind = "Platform" | "Remediation";

export interface ScriptRef {
  kind: ScriptKind;
  id: string;
}

/** Canonical "{Kind}:{Id}" form used everywhere as a stable map key. */
export function formatRefKey(r: ScriptRef): string {
  return `${r.kind}:${r.id}`;
}

/**
 * Map keyed by the canonical "{Kind}:{Id}" form. Value is the display name or null
 * when the ref couldn't be resolved (permission missing / NotFound / transient).
 */
export type DisplayNamesByRefKey = Record<string, string | null>;

/**
 * Walks a list of session events and emits the distinct script refs we'd want display
 * names for. Event shape is intentionally loose because event.data is not strongly typed.
 */
export function extractScriptRefsFromEvents(events: readonly { eventType?: string; data?: unknown }[]): ScriptRef[] {
  const seen = new Map<string, ScriptRef>();
  for (const evt of events) {
    if (
      evt.eventType !== "script_started"
      && evt.eventType !== "script_completed"
      && evt.eventType !== "script_failed"
    ) continue;

    const d = evt.data as Record<string, unknown> | null | undefined;
    if (!d) continue;

    const policyId = pickString(d, ["policyId", "policy_id"]);
    if (!policyId) continue;

    const scriptType = (pickString(d, ["scriptType", "script_type"]) ?? "platform").toLowerCase();
    const kind: ScriptKind = scriptType === "remediation" ? "Remediation" : "Platform";
    const key = `${kind}:${policyId}`;
    if (!seen.has(key)) {
      seen.set(key, { kind, id: policyId });
    }
  }
  return Array.from(seen.values());
}

function pickString(d: Record<string, unknown>, keys: string[]): string | undefined {
  for (const k of keys) {
    const v = d[k];
    if (typeof v === "string" && v.length > 0) return v;
  }
  return undefined;
}

interface DisplayNamesResponse {
  refs: Record<string, string | null>;
  malformed?: string[];
}

/** Backend's per-request cap on ref count (mirrors `MaxRefsPerRequest` in the C# function). */
export const SCRIPT_DISPLAY_NAMES_CHUNK_SIZE = 200;

/**
 * POSTs the resolve request to the backend. Returns a map keyed by the canonical
 * `Kind:id` form. Returns an empty map on transport / non-2xx errors so callers can
 * fall back to "show the ID" without surfacing an error to the user.
 *
 * Refs above {@link SCRIPT_DISPLAY_NAMES_CHUNK_SIZE} are split into batches and POSTed
 * sequentially -- the first batch warms the backend cache for the tenant so subsequent
 * batches stay on the cache-hit path and don't compound Graph throttle pressure. Partial
 * success is preserved: if one batch fails the loop bails but earlier batch results are
 * still returned (better than discarding successful work).
 */
export async function fetchScriptDisplayNames(
  tenantId: string,
  refs: ScriptRef[],
  getAccessToken: GetAccessToken,
): Promise<DisplayNamesByRefKey> {
  if (refs.length === 0) return {};

  const url = api.graphPermissions.scriptDisplayNames(tenantId);
  const merged: DisplayNamesByRefKey = {};

  for (let i = 0; i < refs.length; i += SCRIPT_DISPLAY_NAMES_CHUNK_SIZE) {
    const chunk = refs.slice(i, i + SCRIPT_DISPLAY_NAMES_CHUNK_SIZE);

    try {
      const response = await authenticatedFetch(url, getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refs: chunk.map(formatRefKey) }),
      });
      if (!response.ok) {
        // Non-2xx: preserve earlier chunks, stop hammering. UI degrades to "show IDs".
        break;
      }
      const body = (await response.json()) as DisplayNamesResponse;
      if (body.refs) {
        Object.assign(merged, body.refs);
      }
    } catch (err) {
      // Auth signal MUST propagate so the hook can short-circuit and the app's auth flow
      // can react (token refresh / sign-in redirect). Any partial map collected so far is
      // sacrificed -- the user is being signed out anyway.
      if (err instanceof TokenExpiredError) throw err;
      // Transport-level failure (network timeout, abort, JSON parse, ...): keep whatever
      // earlier chunks produced and stop. Without this break the exception would bubble to
      // the hook's outer catch and discard partial results -- defeating the chunked design.
      break;
    }
  }

  return merged;
}

/**
 * React hook: collects refs from `events`, fires a single backend lookup, and exposes
 * a lookup map keyed by the canonical "{Kind}:{Id}" form. Same id under different kinds
 * stays distinct -- callers must always supply both kind and id when looking up.
 *
 * Stale-state protection: the hook keys its effect off a STABLE refset-fingerprint
 * (`refsKey`) instead of the raw `events` array identity, which the parent typically
 * re-creates on every render via `.filter()`. The map is cleared the moment the tenant
 * changes or the refset changes (including the "now empty" case) so a session switch
 * cannot leave stale names hanging on coincidentally-overlapping ids.
 */
export function useScriptDisplayNames(
  tenantId: string | null | undefined,
  events: readonly { eventType?: string; data?: unknown }[],
  getAccessToken: GetAccessToken,
): DisplayNamesByRefKey {
  const [byRefKey, setByRefKey] = useState<DisplayNamesByRefKey>({});

  // Memo on `events`: extract once per real change. The returned refs array identity is
  // stable across re-renders that produce the same set of script events.
  const refs = useMemo(() => extractScriptRefsFromEvents(events), [events]);

  // Stable string fingerprint of the refset (sorted so order changes don't churn the effect).
  const refsKey = useMemo(
    () => refs.map(formatRefKey).sort().join("|"),
    [refs],
  );

  useEffect(() => {
    // ALWAYS clear before deciding what to do. Covers two staleness cases:
    //   (a) Tenant changed: previous tenant's names must not linger on new tenant's rows.
    //   (b) Refset changed to empty (events not yet loaded, or filtered to none): we have
    //       no authoritative data anymore, so the previous map is stale.
    setByRefKey({});

    if (!tenantId || refsKey.length === 0) return;

    // Reconstruct refs from the stable key so this effect doesn't depend on the
    // `refs` array identity (which is already covered transitively via refsKey).
    const stableRefs: ScriptRef[] = refsKey.split("|").map(token => {
      const idx = token.indexOf(":");
      const kind = (idx > 0 ? token.slice(0, idx) : "Platform") as ScriptKind;
      const id = idx > 0 ? token.slice(idx + 1) : token;
      return { kind, id };
    });

    let cancelled = false;
    (async () => {
      try {
        const refsMap = await fetchScriptDisplayNames(tenantId, stableRefs, getAccessToken);
        if (cancelled) return;
        setByRefKey(refsMap);
      } catch (err) {
        if (err instanceof TokenExpiredError) return; // user signed out / token expired
        console.warn("useScriptDisplayNames: lookup failed", err);
      }
    })();

    return () => { cancelled = true; };
  }, [tenantId, refsKey, getAccessToken]);

  return byRefKey;
}

/**
 * Convenience lookup: given the renderer's `scriptType` ("platform" | "remediation")
 * and the policy ID it sees in events, returns the resolved display name or null.
 * Keeps callers out of the kind-string-formatting business.
 */
export function lookupScriptDisplayName(
  byRefKey: DisplayNamesByRefKey | undefined,
  scriptType: string | undefined,
  policyId: string | undefined,
): string | null {
  if (!byRefKey || !policyId) return null;
  const kind: ScriptKind = scriptType === "remediation" ? "Remediation" : "Platform";
  return byRefKey[`${kind}:${policyId}`] ?? null;
}
