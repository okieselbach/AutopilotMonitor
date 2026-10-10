/**
 * Integration tests for MCP session tools.
 * Tests against the live backend — requires AUTOPILOT_API_TOKEN env var.
 */
import { describe, it, expect, beforeAll } from 'vitest';
import { apiFetch, getToken } from './helpers.js';

// Shared state discovered during tests
let knownSessionId: string;

// Integration suite: only runs when a backend token is supplied. Without one it
// SKIPS (not errors), so an unattended CI run still goes green on the unit
// suites instead of failing the whole file. (review finding tests-7)
const RUN_INTEGRATION = !!process.env.AUTOPILOT_API_TOKEN;
const suite = describe.skipIf(!RUN_INTEGRATION);

beforeAll(() => {
  if (RUN_INTEGRATION) getToken(); // Fail fast if no token
});

suite('search_sessions', () => {
  it('should return sessions (cross-tenant, no filters)', async () => {
    const data = await apiFetch<any>(`/api/global/search/sessions?limit=3`);
    expect(data.success).toBe(true);
    expect(Array.isArray(data.sessions)).toBe(true);
    expect(data.sessions.length).toBeGreaterThan(0);
    expect(data.sessions.length).toBeLessThanOrEqual(3);

    // Store for subsequent tests
    knownSessionId = data.sessions[0].sessionId;

    // Validate session shape
    const s = data.sessions[0];
    expect(s).toHaveProperty('sessionId');
    expect(s).toHaveProperty('tenantId');
    expect(s).toHaveProperty('status');
    expect(s).toHaveProperty('serialNumber');
  });

  it('should filter by status', async () => {
    const data = await apiFetch<any>(`/api/global/search/sessions?status=Failed&limit=2`);
    expect(data.success).toBe(true);
    for (const s of data.sessions) {
      expect(s.status).toBe('Failed');
    }
  });

  it('should return empty for impossible filter', async () => {
    const data = await apiFetch<any>(
      `/api/global/search/sessions?serialNumber=ZZZZZ_NONEXISTENT_99999&limit=1`,
    );
    expect(data.success).toBe(true);
    expect(data.sessions).toHaveLength(0);
  });
});

suite('search_sessions_by_event', () => {
  it('should find sessions with enrollment_complete events', async () => {
    const data = await apiFetch<any>(
      `/api/global/search/sessions-by-event?eventType=enrollment_complete&limit=3`,
    );
    expect(data.success).toBe(true);
    expect(Array.isArray(data.sessions)).toBe(true);
  });

  it('should return empty for unknown event type', async () => {
    const data = await apiFetch<any>(
      `/api/global/search/sessions-by-event?eventType=totally_fake_event&limit=1`,
    );
    expect(data.success).toBe(true);
    expect(data.count).toBe(0);
  });
});

suite('get_session', () => {
  it('should return session details', async () => {
    const data = await apiFetch<any>(`/api/sessions/${knownSessionId}`);
    expect(data.success).toBe(true);
    expect(data.session).toBeDefined();
    expect(data.session.sessionId).toBe(knownSessionId);
  });

  it('should return 404 for non-existent session', async () => {
    const data = await apiFetch<any>(
      `/api/sessions/00000000-0000-0000-0000-000000000000`,
      { expectStatus: 404 },
    );
    expect(data.success).toBe(false);
  });

  it('should reject invalid UUID format', async () => {
    const data = await apiFetch<any>(
      `/api/sessions/not-a-guid`,
      { expectStatus: 400 },
    );
    expect(data.success).toBe(false);
  });
});

suite('get_session_events', () => {
  it('should return events for a session', async () => {
    const data = await apiFetch<any>(`/api/sessions/${knownSessionId}/events`);
    expect(data.success).toBe(true);
    expect(data.sessionId).toBe(knownSessionId);
    expect(Array.isArray(data.events)).toBe(true);
    expect(data.count).toBeGreaterThan(0);
  });

  it('should have correct tenantId (not composite key)', async () => {
    const data = await apiFetch<any>(`/api/sessions/${knownSessionId}/events`);
    expect(data.events.length).toBeGreaterThan(0);
    const event = data.events[0];
    // TenantId should NOT contain underscore + sessionId (composite key bug)
    expect(event.tenantId).not.toContain('_');
    expect(event.tenantId).toHaveLength(36); // UUID length
  });

  it('should return events for cross-tenant session without tenantId (Global Admin fallback)', async () => {
    // This tests the cross-tenant fix we deployed
    const data = await apiFetch<any>(`/api/sessions/${knownSessionId}/events`);
    expect(data.success).toBe(true);
    expect(data.count).toBeGreaterThan(0);
  });

  it('should return 0 events for non-existent session (not an error)', async () => {
    // A valid GUID that doesn't exist should return success with 0 events
    const data = await apiFetch<any>(
      `/api/sessions/00000000-0000-0000-0000-000000000001/events`,
    );
    expect(data.success).toBe(true);
    expect(data.count).toBe(0);
  });
});

suite('get_session_analysis', () => {
  it('should return analysis results', async () => {
    const data = await apiFetch<any>(`/api/sessions/${knownSessionId}/analysis`);
    expect(data.success).toBe(true);
    expect(data.sessionId).toBe(knownSessionId);
    expect(Array.isArray(data.results)).toBe(true);
  });
});

suite('search_sessions_by_cve', () => {
  it('should return results (possibly empty) for a CVE search', async () => {
    const data = await apiFetch<any>(
      `/api/global/search/sessions-by-cve?cveId=CVE-2024-21447&limit=3`,
    );
    expect(data.success).toBe(true);
    expect(Array.isArray(data.sessions)).toBe(true);
  });

  it('should return empty for nonexistent CVE', async () => {
    const data = await apiFetch<any>(
      `/api/global/search/sessions-by-cve?cveId=CVE-9999-99999&limit=1`,
    );
    expect(data.success).toBe(true);
    expect(data.count).toBe(0);
  });
});

suite('list_blocked_devices', () => {
  it('should return blocked devices list (cross-tenant)', async () => {
    const data = await apiFetch<any>(`/api/global/devices/blocked`);
    expect(data.success).toBe(true);
    expect(Array.isArray(data.blocked)).toBe(true);
  });
});
