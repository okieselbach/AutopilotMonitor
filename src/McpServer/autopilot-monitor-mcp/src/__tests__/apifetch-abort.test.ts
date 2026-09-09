/**
 * Client cancellation reaches the backend: the caller context's signal (aborted by the access
 * guard when the client closes the request) is folded into every apiFetch attempt and into the
 * automatic 429/503 retry wait, so an abandoned tool call stops its backend work at once.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { apiFetch, runWithCaller } from '../client.js';

afterEach(() => vi.restoreAllMocks());

function caller(signal: AbortSignal) {
  return { token: 'test-token', isGlobalAdmin: true, isGlobalReader: false, signal };
}

describe('apiFetch — caller cancellation', () => {
  it("aborts the in-flight backend fetch when the caller's signal fires", async () => {
    let seen: AbortSignal | undefined;
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => new Promise<Response>((_resolve, reject) => {
      seen = init?.signal ?? undefined;
      seen?.addEventListener('abort', () => reject(seen?.reason), { once: true });
    }));
    const controller = new AbortController();
    const pending = runWithCaller(caller(controller.signal), () => apiFetch<unknown>('/api/sessions'));

    expect(seen).toBeDefined();
    expect(seen?.aborted).toBe(false);
    controller.abort();
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(seen?.aborted).toBe(true);
  });

  it('ends the automatic retry wait at once on cancellation instead of sleeping out Retry-After', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: false,
      status: 429,
      headers: { get: (name: string) => (name.toLowerCase() === 'retry-after' ? '5' : null) },
      text: async () => '',
    } as unknown as Response);
    const controller = new AbortController();
    const started = Date.now();
    const pending = runWithCaller(caller(controller.signal), () => apiFetch<unknown>('/api/sessions'));
    setTimeout(() => controller.abort(), 20);

    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(Date.now() - started).toBeLessThan(2000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('keeps the plain per-attempt timeout when the caller context carries no signal', async () => {
    let seen: AbortSignal | undefined;
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_url, init) => {
      seen = init?.signal ?? undefined;
      return { ok: true, status: 200, headers: { get: () => null }, text: async () => '{"ok":true}' } as unknown as Response;
    });
    await runWithCaller({ token: 'test-token', isGlobalAdmin: true }, () => apiFetch<unknown>('/api/sessions'));
    expect(seen).toBeDefined();
    expect(seen?.aborted).toBe(false);
  });
});
