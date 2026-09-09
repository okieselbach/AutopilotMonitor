/**
 * query_raw_events pageSize follows the effective projection: a page that carries DataJson is
 * bounded by rows × payload (measured p90 3.3 KB, outliers 21-29 KB per row), so it gets a
 * smaller default and a ceiling of 100, refused BEFORE the backend does the work; a lean page
 * keeps the schema's 1000. A nextLink is judged by its echoed fields.
 *
 * The handler reaches the backend through scanWithTimeoutFallback (client.ts), so that is the
 * seam the test observes: its first argument is the path the backend would receive.
 */
import { describe, it, expect, beforeEach, vi } from 'vitest';

const { scanMock } = vi.hoisted(() => ({ scanMock: vi.fn() }));

vi.mock('../client.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../client.js')>();
  return { ...actual, scanWithTimeoutFallback: scanMock };
});

import { registerAdminTools, rawEventsPayloadIncluded, RAW_EVENTS_PAYLOAD_PAGE_DEFAULT, RAW_EVENTS_PAYLOAD_PAGE_MAX } from '../tools/admin.js';
import { DEFAULT_FIRST_PAGE_SIZE } from '../client.js';

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;

function queryRawEvents(): Handler {
  const handlers: Record<string, Handler> = {};
  const fake = { registerTool: (name: string, _def: unknown, handler: Handler) => { handlers[name] = handler; } };
  registerAdminTools(fake as never, true, true, false);
  return handlers.query_raw_events;
}

const PAGE = { count: 1, events: [{ EventType: 'app_install_failed' }] };

function sentPath(): string {
  return scanMock.mock.calls[0][0] as string;
}

function sentPageSize(): string | null {
  const path = sentPath();
  return new URLSearchParams(path.slice(path.indexOf('?') + 1)).get('pageSize');
}

const FULL_ROWS_NEXTLINK = '/api/raw/events?eventType=app_install_failed&pageSize=1000&continuation=c1';
const LEAN_NEXTLINK = '/api/raw/events?eventType=app_install_failed&fields=EventType%2CSeverity%2COccurredUtc&pageSize=1000&continuation=c1';

describe('rawEventsPayloadIncluded', () => {
  it('follows an explicit fields list, case-insensitively', () => {
    expect(rawEventsPayloadIncluded('PartitionKey,DataJson', undefined, false)).toBe(true);
    expect(rawEventsPayloadIncluded('EventType, datajson', undefined, false)).toBe(true);
    expect(rawEventsPayloadIncluded('EventType,Message', undefined, true)).toBe(false);
  });

  it('a filtered first page without fields carries the payload; an unfiltered one is lean by default', () => {
    expect(rawEventsPayloadIncluded(undefined, undefined, true)).toBe(true);
    expect(rawEventsPayloadIncluded(undefined, undefined, false)).toBe(false);
  });

  it('judges a nextLink by its echoed fields — none echoed means full rows', () => {
    expect(rawEventsPayloadIncluded(undefined, FULL_ROWS_NEXTLINK, false)).toBe(true);
    expect(rawEventsPayloadIncluded(undefined, LEAN_NEXTLINK, false)).toBe(false);
    expect(rawEventsPayloadIncluded(undefined, '/api/raw/events?fields=EventType%2CDataJson&continuation=c1', false)).toBe(true);
  });
});

describe('query_raw_events — pageSize follows the payload projection', () => {
  beforeEach(() => {
    scanMock.mockReset();
    scanMock.mockResolvedValue(PAGE);
  });

  it('refuses pageSize 1000 on a filtered read (full rows) before any backend call', async () => {
    const result = await queryRawEvents()({ eventType: 'app_install_failed', pageSize: 1000 });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain(`exceeds the maximum of ${RAW_EVENTS_PAYLOAD_PAGE_MAX}`);
    expect(scanMock).not.toHaveBeenCalled();
  });

  it('refuses an explicit DataJson projection above the ceiling', async () => {
    const result = await queryRawEvents()({ fields: 'EventType,DataJson', pageSize: RAW_EVENTS_PAYLOAD_PAGE_MAX + 1 });
    expect(result.isError).toBe(true);
    expect(scanMock).not.toHaveBeenCalled();
  });

  it('accepts the ceiling itself with the payload', async () => {
    const result = await queryRawEvents()({ eventType: 'app_install_failed', pageSize: RAW_EVENTS_PAYLOAD_PAGE_MAX });
    expect(result.isError).toBeUndefined();
    expect(sentPageSize()).toBe(String(RAW_EVENTS_PAYLOAD_PAGE_MAX));
  });

  it('keeps the large page for a lean projection', async () => {
    const result = await queryRawEvents()({ eventType: 'app_install_failed', fields: 'EventType,Severity,OccurredUtc', pageSize: 1000 });
    expect(result.isError).toBeUndefined();
    expect(sentPageSize()).toBe('1000');
  });

  it('defaults a payload page to the smaller size and a lean page to the tool default', async () => {
    await queryRawEvents()({ eventType: 'app_install_failed' });
    expect(sentPageSize()).toBe(String(RAW_EVENTS_PAYLOAD_PAGE_DEFAULT));

    scanMock.mockClear();
    await queryRawEvents()({});
    expect(sentPageSize()).toBe(String(DEFAULT_FIRST_PAGE_SIZE));
  });

  it('refuses a nextLink whose embedded pageSize exceeds the ceiling for full rows', async () => {
    const result = await queryRawEvents()({ continuation: FULL_ROWS_NEXTLINK });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain('overrides the value embedded in a nextLink');
    expect(scanMock).not.toHaveBeenCalled();
  });

  it('lets an explicit smaller pageSize override the nextLink and pass', async () => {
    const result = await queryRawEvents()({ continuation: FULL_ROWS_NEXTLINK, pageSize: RAW_EVENTS_PAYLOAD_PAGE_MAX });
    expect(result.isError).toBeUndefined();
    expect(sentPath()).toContain('continuation=c1');
    expect(sentPageSize()).toBe(String(RAW_EVENTS_PAYLOAD_PAGE_MAX));
  });

  it('keeps the embedded 1000 of a lean nextLink', async () => {
    const result = await queryRawEvents()({ continuation: LEAN_NEXTLINK });
    expect(result.isError).toBeUndefined();
    expect(sentPageSize()).toBe('1000');
  });
});
