/**
 * Deployment state of the four components in one call — the data half of the operator's
 * "is what I pushed actually live?" question. Every source is public/anonymous:
 *
 *   backend  live  GET {API}/api/health              → { version, commitHash, buildUtc }
 *   backend  manifest versions/backend.json          → { component, version, commit, buildUtc, deployedUtc, runId }
 *   mcp      live  in-process build info (this process IS the live deployment)
 *   mcp      manifest versions/mcp.json              → … + docsCommit
 *   web      live  GET {PORTAL}/version.json         → { component, commit, buildUtc }
 *   web      manifest versions/web.json              → { component, commit, deployedUtc, runId }
 *   agent    manifest {AGENT}/version.json           → { version, commit, buildUtc, sha256, bootstrapVersion }
 *            (no live endpoint — the agent runs on devices)
 *
 * A manifest is written by the deploy workflow ONLY after it polled the live endpoint until
 * that exact version + commit answered, so "manifest ≠ live" means the running instance drifted
 * AFTER a verified deploy (rollback, stale revision, failed restart) — the case a live-vs-git
 * comparison cannot see. The local-git half (HEAD vs live) stays in the backend-version skill:
 * this server has no checkout.
 */
import { API_BASE_URL, PORTAL_BASE_URL, VERSIONS_BASE_URL, AGENT_DOWNLOAD_BASE_URL } from './config.js';
import { SERVER_VERSION, BUILD_COMMIT, BUILD_UTC, DOCS_COMMIT } from './build-info.js';

export type Drift = 'in-sync' | 'drifted' | 'unknown' | 'not-applicable';

export interface VersionStamp {
  version?: string;
  commit?: string;
  buildUtc?: string;
  deployedUtc?: string;
  runId?: string;
  docsCommit?: string;
  sha256?: string;
  bootstrapVersion?: string;
}

export interface ComponentState {
  live: VersionStamp | null;
  manifest: VersionStamp | null;
  drift: Drift;
  note?: string;
}

export interface DeploymentState {
  checkedAt: string;
  backend: ComponentState;
  mcp: ComponentState;
  web: ComponentState;
  agent: ComponentState;
  /** Source label → error text for every fetch that failed; absent when all succeeded. */
  partialErrors?: Record<string, string>;
}

export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

/** Fetch timeout per source; the MCP is not a fetch source here (in-process), so no cold start to wait for. */
const FETCH_TIMEOUT_MS = 10_000;

/** Ordered like the deploy chain: backend → web → mcp, agent last. */
export const DEPLOYMENT_SOURCES = {
  backendLive: `${API_BASE_URL}/api/health`,
  backendManifest: `${VERSIONS_BASE_URL}/backend.json`,
  webLive: `${PORTAL_BASE_URL}/version.json`,
  webManifest: `${VERSIONS_BASE_URL}/web.json`,
  mcpManifest: `${VERSIONS_BASE_URL}/mcp.json`,
  agentManifest: `${AGENT_DOWNLOAD_BASE_URL}/version.json`,
} as const;

type SourceKey = keyof typeof DEPLOYMENT_SOURCES;

async function fetchJson(fetchImpl: FetchLike, url: string): Promise<Record<string, unknown>> {
  const res = await fetchImpl(url, {
    headers: { Accept: 'application/json' },
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return (await res.json()) as Record<string, unknown>;
}

const str = (v: unknown): string | undefined => (typeof v === 'string' && v.length > 0 ? v : undefined);

/** Backend /api/health spells the commit `commitHash`; every manifest spells it `commit`. */
export function toStamp(raw: Record<string, unknown> | null): VersionStamp | null {
  if (!raw) return null;
  const stamp: VersionStamp = {
    version: str(raw.version),
    commit: str(raw.commit) ?? str(raw.commitHash),
    buildUtc: str(raw.buildUtc),
    deployedUtc: str(raw.deployedUtc),
    runId: str(raw.runId),
    docsCommit: str(raw.docsCommit),
    sha256: str(raw.sha256),
    bootstrapVersion: str(raw.bootstrapVersion),
  };
  // Drop absent keys so the result stays lean.
  for (const k of Object.keys(stamp) as Array<keyof VersionStamp>) if (stamp[k] === undefined) delete stamp[k];
  return stamp;
}

/**
 * Pure drift verdict for one component. Both commits known → compared; the agent (no live
 * endpoint) is not-applicable; anything else is unknown with a note that says which half is
 * missing — never a silent in-sync.
 */
export function assessDrift(live: VersionStamp | null, manifest: VersionStamp | null, opts: { liveApplicable: boolean }): { drift: Drift; note?: string } {
  if (!opts.liveApplicable) return { drift: 'not-applicable', note: 'no live endpoint (runs on devices); the manifest is the release pointer' };
  if (!manifest) return { drift: 'unknown', note: 'manifest unavailable (404 = never deployed since manifests exist)' };
  if (!live) return { drift: 'unknown', note: 'live endpoint unavailable' };
  if (!live.commit || live.commit === 'unknown') return { drift: 'unknown', note: 'live instance reports no commit (local build?)' };
  if (!manifest.commit) return { drift: 'unknown', note: 'manifest carries no commit' };
  if (live.commit === manifest.commit) return { drift: 'in-sync' };
  return {
    drift: 'drifted',
    note: `live commit ${live.commit} ≠ manifest commit ${manifest.commit}: the running instance changed after the verified deploy (rollback, stale revision, failed restart)`,
  };
}

/** The MCP's own live stamp — the process answering this call. */
export function mcpLiveStamp(): VersionStamp {
  return { version: SERVER_VERSION, commit: BUILD_COMMIT, buildUtc: BUILD_UTC, docsCommit: DOCS_COMMIT };
}

/**
 * Collects all sources in parallel (allSettled: one unreachable store must not hide the others),
 * assesses drift per component and lists every failed fetch under partialErrors.
 */
export async function collectDeploymentState(fetchImpl: FetchLike = fetch): Promise<DeploymentState> {
  const keys = Object.keys(DEPLOYMENT_SOURCES) as SourceKey[];
  const settled = await Promise.allSettled(keys.map((k) => fetchJson(fetchImpl, DEPLOYMENT_SOURCES[k])));

  const got: Partial<Record<SourceKey, Record<string, unknown>>> = {};
  const partialErrors: Record<string, string> = {};
  settled.forEach((r, i) => {
    const key = keys[i];
    if (r.status === 'fulfilled') got[key] = r.value;
    else partialErrors[key] = r.reason instanceof Error ? r.reason.message : String(r.reason);
  });

  const backendLive = toStamp(got.backendLive ?? null);
  const backendManifest = toStamp(got.backendManifest ?? null);
  const webLive = toStamp(got.webLive ?? null);
  const webManifest = toStamp(got.webManifest ?? null);
  const mcpManifest = toStamp(got.mcpManifest ?? null);
  const agentManifest = toStamp(got.agentManifest ?? null);
  const mcpLive = mcpLiveStamp();

  const component = (live: VersionStamp | null, manifest: VersionStamp | null, liveApplicable = true): ComponentState =>
    ({ live, manifest, ...assessDrift(live, manifest, { liveApplicable }) });

  const state: DeploymentState = {
    checkedAt: new Date().toISOString(),
    backend: component(backendLive, backendManifest),
    web: component(webLive, webManifest),
    mcp: component(mcpLive, mcpManifest),
    agent: component(null, agentManifest, false),
  };
  if (Object.keys(partialErrors).length) state.partialErrors = partialErrors;
  return state;
}
