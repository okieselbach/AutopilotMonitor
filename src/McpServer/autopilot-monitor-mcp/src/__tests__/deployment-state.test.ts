/**
 * get_deployment_state: the drift arithmetic and the fetch fan-out. fetch is stubbed per URL so
 * every branch is reachable without the network: in-sync, drifted (live ≠ manifest), a manifest
 * 404 (unknown + partialErrors, never a silent in-sync), the backend's `commitHash` spelling,
 * the agent's not-applicable verdict and the MCP's in-process live stamp.
 */
import { describe, it, expect, vi } from 'vitest';
import { assessDrift, collectDeploymentState, toStamp, DEPLOYMENT_SOURCES, mcpLiveStamp } from '../deployment-state.js';
import { SERVER_VERSION } from '../build-info.js';

type Body = Record<string, unknown> | { status: number };

function stubFetch(bodies: Partial<Record<keyof typeof DEPLOYMENT_SOURCES, Body>>): ReturnType<typeof vi.fn> {
  const byUrl = new Map<string, Body>();
  for (const [key, body] of Object.entries(bodies)) byUrl.set(DEPLOYMENT_SOURCES[key as keyof typeof DEPLOYMENT_SOURCES], body!);
  return vi.fn(async (url: string) => {
    const body = byUrl.get(String(url));
    if (!body) return new Response('missing stub', { status: 599 });
    if ('status' in body && Object.keys(body).length === 1) return new Response('', { status: body.status as number });
    return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
  });
}

const ALL_IN_SYNC = {
  backendLive: { status: 'healthy', version: '1.5.1200', commitHash: 'abc1234', buildUtc: '2026-09-04T10:00:00Z' },
  backendManifest: { component: 'backend', version: '1.5.1200', commit: 'abc1234', buildUtc: '2026-09-04T10:00:00Z', deployedUtc: '2026-09-04T10:05:00Z', runId: '1' },
  webLive: { component: 'web', commit: 'def5678', buildUtc: '2026-09-04T09:00:00Z' },
  webManifest: { component: 'web', commit: 'def5678', deployedUtc: '2026-09-04T09:05:00Z', runId: '2' },
  mcpManifest: { component: 'mcp', version: SERVER_VERSION, commit: 'unknown', buildUtc: 'unknown', docsCommit: 'ddd1111', deployedUtc: '2026-09-04T11:00:00Z', runId: '3' },
  agentManifest: { version: '2.4.900', commit: '9999999', buildUtc: '2026-09-01T00:00:00Z', sha256: 'ff', bootstrapVersion: '2.1' },
};

describe('toStamp', () => {
  it('normalizes the backend commitHash spelling and drops absent keys', () => {
    expect(toStamp({ version: '1.5.1', commitHash: 'abc1234', buildUtc: 'x' })).toEqual({ version: '1.5.1', commit: 'abc1234', buildUtc: 'x' });
    expect(toStamp({ commit: 'def5678', runId: 42 })).toEqual({ commit: 'def5678' });
    expect(toStamp(null)).toBeNull();
  });
});

describe('assessDrift', () => {
  it('compares commits when both halves are known', () => {
    expect(assessDrift({ commit: 'a' }, { commit: 'a' }, { liveApplicable: true })).toEqual({ drift: 'in-sync' });
    const d = assessDrift({ commit: 'a' }, { commit: 'b' }, { liveApplicable: true });
    expect(d.drift).toBe('drifted');
    expect(d.note).toContain('a ≠ manifest commit b');
  });

  it('is never silently in-sync when a half is missing', () => {
    expect(assessDrift(null, { commit: 'a' }, { liveApplicable: true }).drift).toBe('unknown');
    expect(assessDrift({ commit: 'a' }, null, { liveApplicable: true })).toMatchObject({ drift: 'unknown', note: expect.stringContaining('404') });
    expect(assessDrift({ commit: 'unknown' }, { commit: 'a' }, { liveApplicable: true })).toMatchObject({ drift: 'unknown', note: expect.stringContaining('local build') });
    expect(assessDrift({ commit: 'a' }, { version: '1' }, { liveApplicable: true }).drift).toBe('unknown');
  });

  it('marks the agent not-applicable', () => {
    expect(assessDrift(null, { commit: 'a' }, { liveApplicable: false }).drift).toBe('not-applicable');
  });
});

describe('collectDeploymentState', () => {
  it('fetches all six sources in parallel and reports every component', async () => {
    const fetchMock = stubFetch(ALL_IN_SYNC);

    const state = await collectDeploymentState(fetchMock as never);

    expect(fetchMock).toHaveBeenCalledTimes(6);
    expect(state.backend).toMatchObject({ drift: 'in-sync', live: { version: '1.5.1200', commit: 'abc1234' }, manifest: { commit: 'abc1234', runId: '1' } });
    expect(state.web).toMatchObject({ drift: 'in-sync', live: { commit: 'def5678' }, manifest: { commit: 'def5678' } });
    expect(state.agent).toMatchObject({ drift: 'not-applicable', live: null, manifest: { version: '2.4.900', sha256: 'ff', bootstrapVersion: '2.1' } });
    // The MCP's live half is this process (a test run has no BUILD_COMMIT → unknown → never in-sync).
    expect(state.mcp.live).toEqual(mcpLiveStamp());
    expect(state.mcp.manifest?.docsCommit).toBe('ddd1111');
    expect(['unknown', 'in-sync']).toContain(state.mcp.drift);
    expect(state.partialErrors).toBeUndefined();
    expect(state.checkedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
  });

  it('reports drift when the live commit differs from the verified manifest', async () => {
    const state = await collectDeploymentState(stubFetch({
      ...ALL_IN_SYNC,
      backendLive: { version: '1.5.1199', commitHash: 'old0000', buildUtc: 'x' },
    }) as never);

    expect(state.backend.drift).toBe('drifted');
    expect(state.backend.note).toContain('old0000 ≠ manifest commit abc1234');
  });

  it('a manifest 404 lands in partialErrors and as unknown, and never hides the other components', async () => {
    const state = await collectDeploymentState(stubFetch({ ...ALL_IN_SYNC, webManifest: { status: 404 } }) as never);

    expect(state.web).toMatchObject({ drift: 'unknown', manifest: null, live: { commit: 'def5678' } });
    expect(state.web.note).toContain('404');
    expect(state.partialErrors).toEqual({ webManifest: 'HTTP 404' });
    expect(state.backend.drift).toBe('in-sync');
  });

  it('an unreachable live endpoint is unknown with the failure listed', async () => {
    const fetchMock = stubFetch(ALL_IN_SYNC);
    fetchMock.mockImplementationOnce(async () => { throw new Error('fetch failed'); });

    const state = await collectDeploymentState(fetchMock as never);

    expect(state.backend.drift).toBe('unknown');
    expect(state.backend.live).toBeNull();
    expect(state.partialErrors?.backendLive).toBe('fetch failed');
  });
});
