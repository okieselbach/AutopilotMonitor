/**
 * Integration tests for MCP admin and metrics tools.
 * Tests against the live backend — requires AUTOPILOT_API_TOKEN env var.
 */
import { describe, it, expect, beforeAll } from 'vitest';
import { apiFetch, getToken } from './helpers.js';

// Integration suite: only runs when a backend token is supplied. Without one it
// SKIPS (not errors), so an unattended CI run still goes green on the unit
// suites instead of failing the whole file. (review finding tests-7)
const RUN_INTEGRATION = !!process.env.AUTOPILOT_API_TOKEN;
const suite = describe.skipIf(!RUN_INTEGRATION);

beforeAll(() => {
  if (RUN_INTEGRATION) getToken();
});

suite('get_metrics', () => {
  it('should return global enrollment metrics', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/summary?days=30`);
    expect(data.success).toBe(true);
    expect(Array.isArray(data.summary)).toBe(true);
    if (data.summary.length > 0) {
      expect(data.summary[0]).toHaveProperty('tenantId');
      expect(data.summary[0]).toHaveProperty('totalSessions');
      expect(data.summary[0]).toHaveProperty('failureRate');
    }
  });

  it('should return app metrics', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/app?days=30`);
    expect(data.success).toBe(true);
    expect(data).toHaveProperty('totalApps');
    expect(data).toHaveProperty('slowestApps');
    expect(data).toHaveProperty('topFailingApps');
  });
});

suite('get_usage_metrics', () => {
  it('should return platform usage overview', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/usage`);
    expect(data).toHaveProperty('sessions');
    expect(data).toHaveProperty('tenants');
    expect(data).toHaveProperty('users');
    expect(data.sessions).toHaveProperty('total');
    expect(data.tenants).toHaveProperty('total');
  });
});

suite('get_geographic_metrics', () => {
  it('should return geographic distribution (by country)', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/geographic?groupBy=country&days=30`);
    expect(data.success).toBe(true);
    expect(Array.isArray(data.locations)).toBe(true);
    if (data.locations.length > 0) {
      const loc = data.locations[0];
      expect(loc).toHaveProperty('locationKey');
      expect(loc).toHaveProperty('sessionCount');
      expect(loc).toHaveProperty('successRate');
    }
  });
});

suite('get_geographic_sessions', () => {
  it('should return sessions for a country', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/geographic/sessions?country=DE&days=7`);
    expect(data.success).toBe(true);
    expect(Array.isArray(data.sessions)).toBe(true);
  });
});

suite('get_platform_metrics', () => {
  it('should return per-session agent metrics with the honest scan count', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/platform`);
    expect(Array.isArray(data.sessions)).toBe(true);
    expect(data).toHaveProperty('sessionsScanned');
    expect(data).toHaveProperty('windowDays');
    expect(data).toHaveProperty('sessionLimit');
    if (data.sessions.length > 0) {
      const s = data.sessions[0];
      expect(s).toHaveProperty('avgCpu');
      expect(s).toHaveProperty('avgWorkingSet');
      expect(s).toHaveProperty('avgSpoolDepth');
    }
  });
});

suite('get_agent_efficiency_metrics', () => {
  it('should return per-version efficiency buckets with percentiles', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/agent-efficiency?days=30&limit=100`);
    expect(data).toHaveProperty('sessionsScanned');
    expect(data).toHaveProperty('sessionsWithSnapshots');
    expect(Array.isArray(data.byVersion)).toBe(true);
    if (data.byVersion.length > 0) {
      const bucket = data.byVersion[0];
      expect(bucket).toHaveProperty('agentVersion');
      expect(bucket).toHaveProperty('sessionsScanned');
      expect(bucket).toHaveProperty('crashRate');
      if (bucket.maxWorkingSetMb) {
        expect(bucket.maxWorkingSetMb).toHaveProperty('p50');
        expect(bucket.maxWorkingSetMb).toHaveProperty('p95');
      }
    }
  });
});

suite('get_api_usage', () => {
  it('should return daily usage summary', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/mcp-usage/daily`);
    expect(Array.isArray(data.summaries)).toBe(true);
    if (data.summaries.length > 0) {
      expect(data.summaries[0]).toHaveProperty('date');
      expect(data.summaries[0]).toHaveProperty('totalRequests');
    }
  });

  it('should return global per-record usage', async () => {
    const data = await apiFetch<any>(`/api/global/metrics/mcp-usage`);
    // Response structure depends on implementation, just verify no error
    expect(data).toBeDefined();
  });
});

suite('get_audit_logs', () => {
  it('should return audit log entries (cross-tenant)', async () => {
    const data = await apiFetch<any>(`/api/global/audit/logs`);
    expect(data.success).toBe(true);
    expect(Array.isArray(data.logs)).toBe(true);
    if (data.logs.length > 0) {
      const log = data.logs[0];
      expect(log).toHaveProperty('action');
      expect(log).toHaveProperty('entityType');
      expect(log).toHaveProperty('performedBy');
      expect(log).toHaveProperty('timestamp');
    }
  });
});

suite('get_ops_events', () => {
  it('should return ops events', async () => {
    const data = await apiFetch<any>(`/api/global/ops-events?maxResults=5`);
    expect(data.success).toBe(true);
    expect(Array.isArray(data.events)).toBe(true);
  });

  it('should filter by category', async () => {
    const data = await apiFetch<any>(`/api/global/ops-events?category=Security&maxResults=5`);
    expect(data.success).toBe(true);
    for (const evt of data.events) {
      expect(evt.category).toBe('Security');
    }
  });
});

suite('list_session_reports', () => {
  it('should return session reports', async () => {
    const data = await apiFetch<any>(`/api/global/session-reports`);
    expect(data.success).toBe(true);
    expect(Array.isArray(data.reports)).toBe(true);
    if (data.reports.length > 0) {
      expect(data.reports[0]).toHaveProperty('reportId');
      expect(data.reports[0]).toHaveProperty('sessionId');
      expect(data.reports[0]).toHaveProperty('tenantId');
    }
  });
});
