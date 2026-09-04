/**
 * Unit tests for the session-diagnostics feature:
 *  - get_session_diagnostics tool (no-blob branch, ticket→downloadUrl assembly, role-scoped POST)
 *  - the static diag_zip_layout catalog (zipMap)
 *  - debug-session prompt registration
 *
 * apiFetch is mocked so these run with no backend / token.
 */
import { describe, it, expect, beforeEach, vi } from 'vitest';

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));

vi.mock('../client.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../client.js')>();
  return { ...actual, apiFetch: apiFetchMock };
});

import { registerSessionTools } from '../tools/sessions.js';
import { registerPrompts } from '../prompts.js';
import { getResourceContent } from '../resource-catalog.js';
import { DIAG_ZIP_MAP } from '../diag-zip-map.js';
import { API_BASE_URL } from '../config.js';

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;

function captureToolHandlers(ga: boolean): Record<string, Handler> {
  const handlers: Record<string, Handler> = {};
  const fake = { registerTool: (name: string, _def: unknown, handler: Handler) => { handlers[name] = handler; } };
  registerSessionTools(fake as never, ga);
  return handlers;
}

function capturePromptNames(ga: boolean): string[] {
  const names: string[] = [];
  const fake = { registerPrompt: (name: string) => { names.push(name); } };
  registerPrompts(fake as never, ga, ga);
  return names;
}

const SID = 'e259c121-1234-4abc-9def-0123456789ab';
const TENANT = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';

function parse(res: { content: Array<{ text: string }> }): any {
  return JSON.parse(res.content[0].text);
}

describe('get_session_diagnostics tool', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('is registered for both tenant users and Global Admins', () => {
    expect(captureToolHandlers(false)).toHaveProperty('get_session_diagnostics');
    expect(captureToolHandlers(true)).toHaveProperty('get_session_diagnostics');
  });

  // Backend wraps the session in a { success, session } envelope (GetSessionFunction).
  const sessionEnvelope = (diagnosticsBlobName: string) =>
    ({ success: true, session: { tenantId: TENANT, diagnosticsBlobName } });

  it('returns available:false when the session has no diagnostics blob', async () => {
    apiFetchMock.mockResolvedValueOnce(sessionEnvelope(''));
    const res = await captureToolHandlers(false).get_session_diagnostics({ sessionId: SID });
    const payload = parse(res);
    expect(payload.available).toBe(false);
    expect(payload.reason).toMatch(/no diagnostics package/i);
    // Only the session lookup happened — no ticket minted.
    expect(apiFetchMock).toHaveBeenCalledTimes(1);
  });

  it('unwraps the { success, session } envelope to find the blob (regression: was read at top-level)', async () => {
    apiFetchMock
      .mockResolvedValueOnce(sessionEnvelope('AgentDiagnostics-x.zip'))
      .mockResolvedValueOnce({ url: '/api/diagnostics/download?t=ABC' });
    const res = await captureToolHandlers(false).get_session_diagnostics({ sessionId: SID });
    expect(parse(res).available).toBe(true);
    expect(apiFetchMock).toHaveBeenCalledTimes(2); // session lookup + ticket mint
  });

  it('rejects a raw (non-enveloped) session object — the envelope IS the wire contract', async () => {
    // Since the typed API contract (GetSessionResponse), { success, session } is the only
    // shape the backend can send. A raw object is a contract violation and must surface
    // as a loud tool error, never be silently absorbed by a legacy fallback.
    apiFetchMock
      .mockResolvedValueOnce({ tenantId: TENANT, diagnosticsBlobName: 'AgentDiagnostics-x.zip' })
      .mockResolvedValueOnce({ url: '/api/diagnostics/download?t=ABC' });
    const res = await captureToolHandlers(false).get_session_diagnostics({ sessionId: SID });
    expect(res.isError).toBe(true);
    expect(apiFetchMock).toHaveBeenCalledTimes(1); // fails before minting a ticket
  });

  it('mints a ticket and returns an absolute downloadUrl + a pointer to the layout resource', async () => {
    apiFetchMock
      .mockResolvedValueOnce(sessionEnvelope('AgentDiagnostics-x.zip'))
      .mockResolvedValueOnce({
        success: true,
        url: '/api/diagnostics/download?t=ABC',
        expiresAt: '2026-06-22T10:10:00Z',
        blobName: 'AgentDiagnostics-x.zip',
        destination: 'CustomerSas',
        sizeBytes: 12345,
      });

    const res = await captureToolHandlers(false).get_session_diagnostics({ sessionId: SID });
    const payload = parse(res);

    expect(payload.available).toBe(true);
    expect(payload.downloadUrl).toBe(`${API_BASE_URL}/api/diagnostics/download?t=ABC`);
    expect(payload.sizeBytes).toBe(12345);
    expect(payload.destination).toBe('CustomerSas');
    // The ~5k-character layout is a static resource, read once per conversation — not repeated per ticket.
    expect(payload.zipMap).toBeUndefined();
    expect(payload.zipLayoutResource).toContain('diag_zip_layout');
    expect(payload.instructions).toMatch(/no auth header/i);
  });

  it('POSTs blobName in the body and scopes the ticket with tenantId', async () => {
    apiFetchMock
      .mockResolvedValueOnce(sessionEnvelope('AgentDiagnostics-x.zip'))
      .mockResolvedValueOnce({ url: '/api/diagnostics/download?t=ABC' });

    await captureToolHandlers(true).get_session_diagnostics({ sessionId: SID, tenantId: TENANT });

    const ticketCall = apiFetchMock.mock.calls[1];
    expect(ticketCall[0]).toContain('/api/diagnostics/download-ticket');
    expect(ticketCall[0]).toContain(`tenantId=${TENANT}`);
    expect(ticketCall[1].method).toBe('POST');
    expect(JSON.parse(ticketCall[1].body).blobName).toBe('AgentDiagnostics-x.zip');
  });

  it('surfaces a backend ticket failure as a tool error', async () => {
    apiFetchMock
      .mockResolvedValueOnce(sessionEnvelope('AgentDiagnostics-x.zip'))
      .mockResolvedValueOnce({ success: true /* no url */ });

    const res = await captureToolHandlers(false).get_session_diagnostics({ sessionId: SID });
    expect(res.isError).toBe(true);
  });
});

describe('diag_zip_layout catalog', () => {
  it('is returned by getResourceContent', () => {
    expect(getResourceContent('diag_zip_layout')).toBe(DIAG_ZIP_MAP);
  });

  it('flags AppWorkload as grep-only with a size warning', () => {
    const appWorkload = DIAG_ZIP_MAP.files.find((f) => f.path.includes('AppWorkload'));
    expect(appWorkload).toBeDefined();
    expect(appWorkload!.read).toBe('grep-only');
    expect(appWorkload!.warning).toMatch(/MB/);
  });

  it('puts final-status.json at priority 1', () => {
    const top = [...DIAG_ZIP_MAP.files].sort((a, b) => a.priority - b.priority)[0];
    expect(top.path).toContain('final-status.json');
  });
});

describe('debug-session prompt', () => {
  it('is registered for both tenant users and Global Admins', () => {
    expect(capturePromptNames(false)).toContain('debug-session');
    expect(capturePromptNames(true)).toContain('debug-session');
  });
});
