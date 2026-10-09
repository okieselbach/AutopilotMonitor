/**
 * search_sessions_by_event reads the EventTypeIndex, and the backend drops index rows whose
 * session no longer exists (deleted, or not yet removed by the orphan sweep). A page can therefore
 * be empty and still carry a nextLink, so the tool must go through the same auto-exhausting
 * scan as search_sessions instead of a single fetch. The scan seam is the observable here.
 */
import { describe, it, expect, beforeEach, vi } from 'vitest';

const { scanMock } = vi.hoisted(() => ({ scanMock: vi.fn() }));

vi.mock('../client.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../client.js')>();
  return { ...actual, scanWithTimeoutFallback: scanMock };
});

import { registerSessionTools } from '../tools/sessions.js';

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;

function searchSessionsByEvent(): Handler {
  const handlers: Record<string, Handler> = {};
  const fake = { registerTool: (name: string, _def: unknown, handler: Handler) => { handlers[name] = handler; } };
  registerSessionTools(fake as never, true);
  return handlers.search_sessions_by_event;
}

const PAGE = { count: 1, sessions: [{ sessionId: 's1', tenantId: 't1' }], scannedPages: 3 };

describe('search_sessions_by_event — auto-exhausts empty-but-continuable pages', () => {
  beforeEach(() => {
    scanMock.mockReset();
    scanMock.mockResolvedValue(PAGE);
  });

  it('reaches the backend through the scanning fetch, not a single apiFetch', async () => {
    const result = await searchSessionsByEvent()({ eventType: 'app_install_failed' });

    expect(result.isError).toBeFalsy();
    expect(scanMock).toHaveBeenCalledTimes(1);
    // Global vs tenant route follows the request's routing context, not the registration flag.
    const [path, basePath] = scanMock.mock.calls[0] as [string, string];
    expect(basePath).toMatch(/^\/api\/(global\/)?search\/sessions-by-event$/);
    expect(path.startsWith(`${basePath}?`)).toBe(true);
    expect(path).toContain('eventType=app_install_failed');
    expect(result.content[0].text).toContain('"scannedPages":3');
  });

  it('keeps the nextLink cursor when continuing', async () => {
    const next = '/api/search/sessions-by-event?eventType=app_install_failed&pageSize=50&continuation=c1';
    await searchSessionsByEvent()({ eventType: 'app_install_failed', tenantId: 't1', continuation: next });

    const [path] = scanMock.mock.calls[0] as [string, string];
    expect(path).toContain('continuation=c1');
    expect(path).toContain('eventType=app_install_failed');
  });
});
