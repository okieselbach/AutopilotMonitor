/**
 * Unit tests for the tenant-config write surface (get_tenant_config /
 * update_tenant_config / list_tenant_config_backups / revert_tenant_config):
 *  - the redacted-view pin: get_tenant_config must ALWAYS request ?view=redacted
 *    (this is the guard that keeps clear-text webhook/SAS secrets out of model
 *    context — the backend serves a GA the full secrets without it)
 *  - request shapes of the mutating tools (method, body, path encoding)
 *  - GUID validation on tenantId (path-traversal guard)
 *
 * apiFetch is mocked so these run with no backend / token.
 */
import { describe, it, expect, beforeEach, vi } from 'vitest';

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));

vi.mock('../client.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../client.js')>();
  return { ...actual, apiFetch: apiFetchMock };
});

import { registerAdminTools } from '../tools/admin.js';
import { TenantGuidSchema } from '../tools/shared.js';

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;

function captureToolHandlers(ga: boolean, strictGa: boolean, delegated = false): Record<string, Handler> {
  const handlers: Record<string, Handler> = {};
  const fake = { registerTool: (name: string, _def: unknown, handler: Handler) => { handlers[name] = handler; } };
  registerAdminTools(fake as never, ga, strictGa, delegated);
  return handlers;
}

const TENANT = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';

describe('config-write tool registration', () => {
  it('all four tools exist for a strict Global Admin only', () => {
    const gaHandlers = captureToolHandlers(true, true);
    const readerHandlers = captureToolHandlers(true, false);
    const tenantHandlers = captureToolHandlers(false, false);
    for (const name of [
      'get_tenant_config', 'update_tenant_config',
      'list_tenant_config_backups', 'revert_tenant_config',
    ]) {
      expect(gaHandlers, `${name} missing for GA`).toHaveProperty(name);
      expect(readerHandlers, `${name} must be hidden from a Global Reader`).not.toHaveProperty(name);
      expect(tenantHandlers, `${name} must be hidden from a tenant user`).not.toHaveProperty(name);
    }
  });
});

describe('get_tenant_config — forced redaction pin', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('always requests ?view=redacted — secrets must never reach model context', async () => {
    apiFetchMock.mockResolvedValueOnce({ tenantId: TENANT, teamsWebhookUrl: '***REDACTED***' });
    await captureToolHandlers(true, true).get_tenant_config({ tenantId: TENANT });

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    const path = apiFetchMock.mock.calls[0][0] as string;
    expect(path).toBe(`/api/config/${TENANT}?view=redacted`);
  });
});

describe('update_tenant_config — request shape', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('PATCHes the fields endpoint with { fields, reason }', async () => {
    apiFetchMock.mockResolvedValueOnce({ success: true, appliedFields: ['DataRetentionDays'], backupId: 'b1' });
    await captureToolHandlers(true, true).update_tenant_config({
      tenantId: TENANT,
      fields: { dataRetentionDays: 90 },
      reason: 'retention bump',
    });

    const [path, options] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe(`/api/config/${TENANT}/fields`);
    expect(options.method).toBe('PATCH');
    expect(JSON.parse(options.body as string)).toEqual({
      fields: { dataRetentionDays: 90 },
      reason: 'retention bump',
    });
  });
});

describe('revert_tenant_config — request shape', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('POSTs the revert endpoint; includeProtectedFields defaults to false', async () => {
    apiFetchMock.mockResolvedValueOnce({ success: true });
    await captureToolHandlers(true, true).revert_tenant_config({
      tenantId: TENANT,
      reason: 'undo bad change',
    });

    const [path, options] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe(`/api/config/${TENANT}/revert`);
    expect(options.method).toBe('POST');
    expect(JSON.parse(options.body as string)).toEqual({
      backupId: undefined,
      includeProtectedFields: false,
      reason: 'undo bad change',
    });
  });

  it('passes backupId and an explicit includeProtectedFields through', async () => {
    apiFetchMock.mockResolvedValueOnce({ success: true });
    await captureToolHandlers(true, true).revert_tenant_config({
      tenantId: TENANT,
      backupId: '0001_abcd1234',
      includeProtectedFields: true,
      reason: 'full restore incl. plan',
    });

    const body = JSON.parse((apiFetchMock.mock.calls[0][1] as RequestInit).body as string);
    expect(body.backupId).toBe('0001_abcd1234');
    expect(body.includeProtectedFields).toBe(true);
  });
});

describe('list_tenant_config_backups — request shape', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('GETs the backups endpoint, forwarding max when provided', async () => {
    apiFetchMock.mockResolvedValue({ tenantId: TENANT, backups: [] });
    const handlers = captureToolHandlers(true, true);

    await handlers.list_tenant_config_backups({ tenantId: TENANT });
    expect(apiFetchMock.mock.calls[0][0]).toBe(`/api/config/${TENANT}/backups`);

    await handlers.list_tenant_config_backups({ tenantId: TENANT, max: 5 });
    expect(apiFetchMock.mock.calls[1][0]).toBe(`/api/config/${TENANT}/backups?max=5`);
  });
});

describe('tenantId GUID validation (path-traversal guard)', () => {
  it('TenantGuidSchema rejects traversal / garbage values', () => {
    for (const bad of ['../admin/foo', 'not-a-guid', '', `${TENANT}/extra`]) {
      expect(TenantGuidSchema.safeParse(bad).success, `"${bad}" must be rejected`).toBe(false);
    }
    expect(TenantGuidSchema.safeParse(TENANT).success).toBe(true);
    expect(TenantGuidSchema.safeParse(TENANT.toUpperCase()).success).toBe(true);
  });
});
