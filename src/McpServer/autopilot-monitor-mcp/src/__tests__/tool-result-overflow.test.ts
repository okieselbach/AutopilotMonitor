/**
 * The response-size cap is enforced, not advisory: a page above the tool's cap is never sent —
 * the answer is an error naming what the page held and the exact pageSize that fits, computed from
 * THIS page's bytes per row. The oversized page's nextLink is withheld (following it would skip
 * rows), and the telemetry marker carries the size the page would have had.
 */
import { describe, it, expect } from 'vitest';
import { toolResultText, OVERFLOW_META_KEY } from '../tools/shared.js';

const CAP = 10_000;

function rows(n: number, payloadChars: number): Array<{ id: number; payload: string }> {
  return Array.from({ length: n }, (_, i) => ({ id: i, payload: 'x'.repeat(payloadChars) }));
}

function bodyOf(result: { content: Array<{ text: string }> }): Record<string, unknown> {
  return JSON.parse(result.content[0].text) as Record<string, unknown>;
}

describe('toolResultText — hard cap', () => {
  it('sends a page under the cap unchanged, with the size hint and no overflow marker', () => {
    const data = { count: 3, events: rows(3, 100), nextLink: '/api/x?continuation=abc' };
    const r = toolResultText(data, CAP);
    expect(r.isError).toBeUndefined();
    expect(bodyOf(r)).toEqual(data);
    expect(r._meta['anthropic/maxResultSizeChars']).toBe(CAP);
    expect(r._meta[OVERFLOW_META_KEY]).toBeUndefined();
  });

  it('refuses a page over the cap: no rows, no nextLink, the pageSize that fits, the marker', () => {
    const data = { count: 100, events: rows(100, 500), nextLink: '/api/x?continuation=abc' };
    const chars = JSON.stringify(data).length;
    const r = toolResultText(data, CAP);

    expect(r.isError).toBe(true);
    const body = bodyOf(r);
    expect(body.overflow).toBe(true);
    expect(body.responseChars).toBe(chars);
    expect(body.maxResultSizeChars).toBe(CAP);
    expect(body.rowsField).toBe('events');
    expect(body.rowsFetched).toBe(100);
    expect(body.rowsThatFit).toBe(Math.floor((CAP * 0.9) / (chars / 100)));
    expect(body.rowsThatFit as number).toBeGreaterThanOrEqual(1);
    expect(body.rowsThatFit as number).toBeLessThan(100);
    expect(body.events).toBeUndefined();
    expect(body.nextLink).toBeUndefined();
    expect(body.advice).toContain(`pageSize=${body.rowsThatFit}`);
    expect(r.content[0].text.length).toBeLessThan(CAP);
    expect(r._meta[OVERFLOW_META_KEY]).toEqual({ responseChars: chars });
    expect(r._meta['anthropic/maxResultSizeChars']).toBe(CAP);
  });

  it('gives an honest advice: a re-send at rowsThatFit lands under the cap', () => {
    const fit = bodyOf(toolResultText({ events: rows(100, 500) }, CAP)).rowsThatFit as number;
    const again = toolResultText({ events: rows(fit, 500) }, CAP);
    expect(again.isError).toBeUndefined();
  });

  it('never advises fewer than one row', () => {
    const body = bodyOf(toolResultText({ events: rows(2, CAP) }, CAP));
    expect(body.rowsThatFit).toBe(1);
  });

  it('gives only narrowing advice when the page carries no list', () => {
    const body = bodyOf(toolResultText({ blob: 'x'.repeat(CAP + 1) }, CAP));
    expect(body.overflow).toBe(true);
    expect(body.rowsThatFit).toBeUndefined();
    expect(body.rowsField).toBeUndefined();
    expect(body.advice).toContain('Narrow the query');
  });

  it('does not project a pageSize from a one-element list (the KQL tables[] shape)', () => {
    const body = bodyOf(toolResultText({ tables: [{ name: 'PrimaryResult', rows: rows(50, 300) }] }, CAP));
    expect(body.overflow).toBe(true);
    expect(body.rowsThatFit).toBeUndefined();
  });

  it('picks the largest top-level list when a page carries several', () => {
    const body = bodyOf(toolResultText({ warnings: ['a', 'b'], sessions: rows(60, 400) }, CAP));
    expect(body.rowsField).toBe('sessions');
    expect(body.rowsFetched).toBe(60);
  });
});
