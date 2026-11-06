/**
 * get_tenant_activity: the shaping of the tenant activity views (tenant-activity-shape.ts) and the tool's routing.
 *  - rows are slim: a ranking row carries the ranking columns, a lifecycle row the figures of its stage;
 *  - absent wire values become explicit nulls (no previous enrollments, no finished session, no sign-in history);
 *  - every lifecycle list has its own order, and equal keys keep the backend order;
 *  - every list is cut to its cap and states its full length, and a realistic platform stays under the response cap;
 *  - platform roles only; days reaches the backend, view and stage stay in the MCP server.
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
import { MAX_RESULT_SIZE_CHARS } from '../tools/shared.js';
import { LIFECYCLE_LISTS, TENANT_ACTIVITY_CAPS, TENANT_ACTIVITY_VIEWS, shapeTenantActivity, type TenantActivityView } from '../tenant-activity-shape.js';
import { TENANT_ACTIVITY_STAGES, type TenantActivityStage } from '../generated/wire-vocabularies.generated.js';
import type { GetTenantActivityResponse, TenantActivityTenantItem } from '../generated/wire-types.generated.js';

type Row = Record<string, unknown>;
interface List { total: number; truncated: boolean; rows: Row[] }

/** Placeholder tenant ids: a GUID per index, never a real tenant. */
const tenantId = (i: number) => `aaaaaaaa-0000-4000-8000-${String(i).padStart(12, '0')}`;

function tenant(i: number, over: Partial<TenantActivityTenantItem> = {}): TenantActivityTenantItem {
  return {
    tenantId: tenantId(i),
    domainName: `tenant${i}.contoso.example`,
    edition: 'community',
    stage: 'active',
    enrollments: 0,
    enrollmentsPrevious: 0,
    succeeded: 0,
    failed: 0,
    activeDays: 0,
    lifetimeEnrollments: 0,
    lifetimeSucceeded: 0,
    lifetimeEvents: 0,
    ...over,
  };
}

function response(tenants: TenantActivityTenantItem[], over: Partial<GetTenantActivityResponse> = {}): GetTenantActivityResponse {
  return {
    windowDays: 30,
    windowStart: '2026-10-07',
    usageDaysThrough: '2026-11-05',
    previousWindowStart: '2026-09-07',
    rowsFrom: '2026-05-10',
    portalActivityAvailable: true,
    totals: {
      signedUpTenants: tenants.length,
      activeTenants: 2,
      activeTenantsPrevious: 1,
      activeTenantsTrendPercent: 100,
      enrollments: 43,
      enrollmentsPrevious: 30,
      enrollmentsTrendPercent: 43,
      succeeded: 38,
      failed: 2,
      successPercent: 95,
      top10SharePercent: 100,
      stages: { neverEnrolled: 1, dormant: 0, new: 0, declining: 0, active: 2, quiet: 0 },
      portalUsers: 5,
      portalTenants: 2,
    },
    daily: [
      { day: '2026-11-04', enrollments: 0, succeeded: 0, failed: 0, activeTenants: 0 },
      { day: '2026-11-05', enrollments: 43, succeeded: 38, failed: 2, activeTenants: 2 },
    ],
    tenants,
    ...over,
  };
}

const stagesOf = (out: Row) => out.stages as Record<string, List>;
const ids = (list: List) => list.rows.map((r) => r.tenantId);

// ── Summary ───────────────────────────────────────────────────────────────────

describe('shapeTenantActivity — summary', () => {
  const contoso = tenant(1, {
    domainName: 'contoso.example', companyName: 'Contoso Ltd', edition: 'pro', stage: 'active',
    onboardedAt: '2026-03-02T09:15:00.1234567Z', enrollments: 40, enrollmentsPrevious: 30, trendPercent: 33,
    succeeded: 36, failed: 2, successPercent: 95, activeDays: 18, dailyEnrollments: [1, 2, 3],
    firstEnrollmentDay: '2026-03-03', lastActiveDay: '2026-11-05', lastCounterUpdateAt: '2026-11-06T07:00:00Z',
    lifetimeEnrollments: 900, lifetimeSucceeded: 850, lifetimeEvents: 120_000, portalUsers: 4, lastPortalVisitAt: '2026-11-06T08:00:00Z',
  });
  // Enrolled, but no previous enrollments, no finished session and no portal figures on the wire.
  const fabrikam = tenant(2, { domainName: 'fabrikam.example', stage: 'new', enrollments: 3, activeDays: 1, lastActiveDay: '2026-11-05' });
  const idle = tenant(3, { stage: 'neverEnrolled', onboardedAt: '2026-11-01T10:00:00Z' });

  it('carries the window, the totals with explicit nulls and the daily series', () => {
    const data = response([contoso, fabrikam, idle]);
    const { activeTenantsTrendPercent: _a, enrollmentsTrendPercent: _e, successPercent: _s, top10SharePercent: _t, portalUsers: _p, portalTenants: _q, ...withoutOptional } = data.totals;
    const out = shapeTenantActivity({ ...data, totals: withoutOptional }, 'summary');

    expect(out.view).toBe('summary');
    expect(out.window).toEqual({ days: 30, start: '2026-10-07', through: '2026-11-05', previousStart: '2026-09-07', rowsFrom: '2026-05-10' });
    expect(out.portalActivityAvailable).toBe(true);
    expect(out.totals).toEqual({
      ...withoutOptional,
      activeTenantsTrendPercent: null,
      enrollmentsTrendPercent: null,
      successPercent: null,
      top10SharePercent: null,
      portalUsers: null,
      portalTenants: null,
    });
    expect(out.daily).toEqual(data.daily);
  });

  it('keeps the totals the backend computed', () => {
    const data = response([contoso]);
    expect(shapeTenantActivity(data, 'summary').totals).toEqual(data.totals);
  });

  it('lists the most active tenants as slim ranking rows, absent figures as null', () => {
    const mostActive = shapeTenantActivity(response([contoso, fabrikam, idle]), 'summary').mostActive as List;

    expect(mostActive).toMatchObject({ total: 2, truncated: false });
    expect(mostActive.rows).toEqual([
      {
        tenantId: tenantId(1), domain: 'contoso.example', edition: 'pro', stage: 'active', enrollments: 40, previous: 30,
        trendPercent: 33, successPercent: 95, activeDays: 18, lastActiveDay: '2026-11-05', portalUsers: 4,
      },
      {
        tenantId: tenantId(2), domain: 'fabrikam.example', edition: 'community', stage: 'new', enrollments: 3, previous: 0,
        trendPercent: null, successPercent: null, activeDays: 1, lastActiveDay: '2026-11-05', portalUsers: null,
      },
    ]);
  });

  it('shows the head of the four lifecycle lists and points to view=lifecycle', () => {
    const out = shapeTenantActivity(response([contoso, fabrikam, idle]), 'summary');
    const stages = stagesOf(out);

    expect(Object.keys(stages)).toEqual([...LIFECYCLE_LISTS]);
    expect(stages.new).toEqual({ total: 1, truncated: false, rows: [{ tenantId: tenantId(2), domain: 'fabrikam.example', edition: 'community', onboarded: null, firstEnrollmentDay: null, enrollments: 3 }] });
    expect(stages.neverEnrolled.rows.map((r) => r.onboarded)).toEqual(['2026-11-01']);
    expect(stages.declining).toEqual({ total: 0, truncated: false, rows: [] });
    expect(out.note).toMatch(/view=lifecycle .* stage=/);
  });
});

// ── Lifecycle rows ────────────────────────────────────────────────────────────

describe('shapeTenantActivity — lifecycle rows', () => {
  const full: Partial<TenantActivityTenantItem> = {
    edition: 'pro-trial', companyName: 'Contoso Ltd', onboardedAt: '2026-08-20T13:45:10.1234567Z', enrollments: 6,
    enrollmentsPrevious: 20, trendPercent: -70, succeeded: 5, failed: 1, successPercent: 83, activeDays: 4,
    dailyEnrollments: [6], firstEnrollmentDay: '2026-10-12', lastActiveDay: '2026-11-04', lastCounterUpdateAt: '2026-11-04T18:00:00Z',
    lifetimeEnrollments: 75, lifetimeSucceeded: 70, lifetimeEvents: 9_000, portalUsers: 3, lastPortalVisitAt: '2026-11-02T07:30:00.5Z',
  };
  const base = { tenantId: tenantId(1), domain: 'tenant1.contoso.example', edition: 'pro-trial' };
  const rowOf = (stage: TenantActivityStage, over: Partial<TenantActivityTenantItem> = full) =>
    stagesOf(shapeTenantActivity(response([tenant(1, { ...over, stage })]), 'lifecycle', stage))[stage].rows[0];

  it('carries id, domain and edition plus the figures of its stage only', () => {
    expect(rowOf('new')).toEqual({ ...base, onboarded: '2026-08-20', firstEnrollmentDay: '2026-10-12', enrollments: 6 });
    expect(rowOf('declining')).toEqual({ ...base, previous: 20, enrollments: 6, trendPercent: -70, lastActiveDay: '2026-11-04' });
    expect(rowOf('dormant')).toEqual({ ...base, lifetimeEnrollments: 75, lastActiveDay: '2026-11-04' });
    expect(rowOf('neverEnrolled')).toEqual({ ...base, onboarded: '2026-08-20', lastPortalVisit: '2026-11-02' });
    expect(rowOf('active')).toEqual({ ...base, enrollments: 6, lastActiveDay: '2026-11-04' });
    expect(rowOf('quiet')).toEqual({ ...base, enrollments: 6, lastActiveDay: '2026-11-04' });
  });

  it('turns absent figures into null', () => {
    const bare = { edition: 'pro-trial' };
    expect(rowOf('new', bare)).toEqual({ ...base, onboarded: null, firstEnrollmentDay: null, enrollments: 0 });
    expect(rowOf('declining', bare)).toEqual({ ...base, previous: 0, enrollments: 0, trendPercent: null, lastActiveDay: null });
    expect(rowOf('dormant', bare)).toEqual({ ...base, lifetimeEnrollments: 0, lastActiveDay: null });
    expect(rowOf('neverEnrolled', bare)).toEqual({ ...base, onboarded: null, lastPortalVisit: null });
  });

  it('without the sign-in history the portal figures are null, never 0', () => {
    const data = response([tenant(1, { stage: 'active', enrollments: 5 }), tenant(2, { stage: 'neverEnrolled' })], { portalActivityAvailable: false });

    const summary = shapeTenantActivity(data, 'summary');
    expect(summary.portalActivityAvailable).toBe(false);
    expect((summary.mostActive as List).rows[0].portalUsers).toBeNull();
    expect(stagesOf(summary).neverEnrolled.rows[0].lastPortalVisit).toBeNull();
    expect((shapeTenantActivity(data, 'ranking').tenants as List).rows[0].portalUsers).toBeNull();
  });

  it('view=lifecycle without stage shows the four lists; with stage only that one', () => {
    const data = response([tenant(1, { stage: 'quiet' }), tenant(2, { stage: 'dormant' })]);

    const four = shapeTenantActivity(data, 'lifecycle');
    expect(Object.keys(stagesOf(four))).toEqual([...LIFECYCLE_LISTS]);
    expect(four.note).toMatch(/active and quiet/);
    expect(four).not.toHaveProperty('totals');

    const quiet = shapeTenantActivity(data, 'lifecycle', 'quiet');
    expect(Object.keys(stagesOf(quiet))).toEqual(['quiet']);
    expect(ids(stagesOf(quiet).quiet)).toEqual([tenantId(1)]);
  });
});

// ── List order ────────────────────────────────────────────────────────────────

describe('shapeTenantActivity — list order', () => {
  const order = (stage: TenantActivityStage, tenants: TenantActivityTenantItem[]) =>
    ids(stagesOf(shapeTenantActivity(response(tenants), 'lifecycle', stage))[stage]);

  it('new: the latest first day first; no first day (sessions after the last daily row) before all', () => {
    expect(order('new', [
      tenant(1, { stage: 'new', firstEnrollmentDay: '2026-10-10' }),
      tenant(2, { stage: 'new' }),
      tenant(3, { stage: 'new', firstEnrollmentDay: '2026-11-01' }),
      tenant(4, { stage: 'new', firstEnrollmentDay: '2026-10-10' }),
    ])).toEqual([tenantId(2), tenantId(3), tenantId(1), tenantId(4)]);
  });

  it('declining: the largest drop against the previous window first', () => {
    expect(order('declining', [
      tenant(1, { stage: 'declining', enrollmentsPrevious: 10, enrollments: 5 }),
      tenant(2, { stage: 'declining', enrollmentsPrevious: 20, enrollments: 2 }),
      tenant(3, { stage: 'declining', enrollmentsPrevious: 8, enrollments: 4 }),
      tenant(4, { stage: 'declining', enrollmentsPrevious: 12, enrollments: 7 }),
    ])).toEqual([tenantId(2), tenantId(1), tenantId(4), tenantId(3)]);
  });

  it('dormant: the most enrollments since signup first', () => {
    expect(order('dormant', [
      tenant(1, { stage: 'dormant', lifetimeEnrollments: 40 }),
      tenant(2, { stage: 'dormant', lifetimeEnrollments: 300 }),
      tenant(3, { stage: 'dormant', lifetimeEnrollments: 40 }),
      tenant(4, { stage: 'dormant', lifetimeEnrollments: 7 }),
    ])).toEqual([tenantId(2), tenantId(1), tenantId(3), tenantId(4)]);
  });

  it('neverEnrolled: the newest signup first, by instant; a row without a signup time last', () => {
    expect(order('neverEnrolled', [
      tenant(1, { stage: 'neverEnrolled' }),
      tenant(2, { stage: 'neverEnrolled', onboardedAt: '2026-10-01T10:00:00Z' }),
      tenant(3, { stage: 'neverEnrolled', onboardedAt: '2026-10-01T10:00:00.5000000Z' }),
      tenant(4, { stage: 'neverEnrolled', onboardedAt: '2026-11-02T08:00:00Z' }),
      tenant(5, { stage: 'neverEnrolled', onboardedAt: '2026-10-01T10:00:00Z' }),
    ])).toEqual([tenantId(4), tenantId(3), tenantId(2), tenantId(5), tenantId(1)]);
  });

  it('active and quiet keep the backend order; the input is not reordered', () => {
    const tenants = [
      tenant(1, { stage: 'active', enrollments: 2, lifetimeEnrollments: 900 }),
      tenant(2, { stage: 'active', enrollments: 9, lifetimeEnrollments: 10 }),
      tenant(3, { stage: 'quiet', lastActiveDay: '2026-11-06' }),
      tenant(4, { stage: 'quiet', lastActiveDay: '2026-10-01' }),
    ];
    expect(order('active', tenants)).toEqual([tenantId(1), tenantId(2)]);
    expect(order('quiet', tenants)).toEqual([tenantId(3), tenantId(4)]);
    expect(tenants.map((t) => t.tenantId)).toEqual([1, 2, 3, 4].map(tenantId));
  });
});

// ── Cuts ──────────────────────────────────────────────────────────────────────

describe('shapeTenantActivity — every list is cut to its cap with its full length', () => {
  it('the ranking: tenants with enrollments only, in the backend order, 15 in the summary and 300 in view=ranking', () => {
    const enrolled = Array.from({ length: 320 }, (_, i) => tenant(i, { enrollments: 400 - i }));
    const data = response([...enrolled, tenant(900, { stage: 'neverEnrolled' })]);

    const mostActive = shapeTenantActivity(data, 'summary').mostActive as List;
    expect(mostActive).toMatchObject({ total: 320, truncated: true });
    expect(mostActive.rows).toHaveLength(TENANT_ACTIVITY_CAPS.mostActive);
    expect(TENANT_ACTIVITY_CAPS.mostActive).toBe(15);

    const ranking = shapeTenantActivity(data, 'ranking').tenants as List;
    expect(ranking).toMatchObject({ total: 320, truncated: true });
    expect(ranking.rows).toHaveLength(300);
    expect(ids(ranking)).toEqual(enrolled.slice(0, 300).map((t) => t.tenantId));
  });

  it('a lifecycle list: 10 in the summary, 100 in view=lifecycle, 500 with stage', () => {
    const data = response(Array.from({ length: 520 }, (_, i) => tenant(i, { stage: 'neverEnrolled' })));

    const head = stagesOf(shapeTenantActivity(data, 'summary')).neverEnrolled;
    expect(head).toMatchObject({ total: 520, truncated: true });
    expect(head.rows).toHaveLength(10);

    const lifecycle = stagesOf(shapeTenantActivity(data, 'lifecycle')).neverEnrolled;
    expect(lifecycle).toMatchObject({ total: 520, truncated: true });
    expect(lifecycle.rows).toHaveLength(100);

    const one = stagesOf(shapeTenantActivity(data, 'lifecycle', 'neverEnrolled')).neverEnrolled;
    expect(one).toMatchObject({ total: 520, truncated: true });
    expect(one.rows).toHaveLength(500);
  });

  it('a list under its cap is whole and not truncated', () => {
    const data = response(Array.from({ length: 100 }, (_, i) => tenant(i, { stage: 'dormant' })));

    expect(stagesOf(shapeTenantActivity(data, 'lifecycle')).dormant).toMatchObject({ total: 100, truncated: false });
    expect(stagesOf(shapeTenantActivity(data, 'lifecycle')).dormant.rows).toHaveLength(100);
    expect(stagesOf(shapeTenantActivity(data, 'lifecycle', 'dormant')).dormant).toMatchObject({ total: 100, truncated: false });
  });
});

// ── Size ──────────────────────────────────────────────────────────────────────

/** A realistic large platform: 1,000 signups, a 90-day window, full wire rows (about 500 characters, 950 with the daily series). */
function platform(): GetTenantActivityResponse {
  const mix: ReadonlyArray<readonly [TenantActivityStage, number]> = [
    ['neverEnrolled', 600], ['dormant', 150], ['new', 50], ['declining', 50], ['active', 120], ['quiet', 30],
  ];
  const editions = ['community', 'pro', 'pro-trial', 'pro-msp'];
  const tenants: TenantActivityTenantItem[] = [];
  let i = 0;
  for (const [stage, count] of mix) {
    for (let k = 0; k < count; k++, i++) {
      const enrollments = stage === 'active' ? 2_000 + i : stage === 'new' ? 40 + k : stage === 'declining' ? 9 : 0;
      const enrolledEver = stage !== 'neverEnrolled';
      tenants.push({
        tenantId: `${(i % 16).toString(16).repeat(8)}-1234-4abc-9def-${String(i).padStart(12, '0')}`,
        domainName: `subsidiary-${String(i).padStart(4, '0')}-manufacturing.contoso.example`,
        companyName: `Contoso Manufacturing Subsidiary ${i} International GmbH`,
        edition: editions[i % editions.length],
        stage,
        onboardedAt: '2026-04-17T13:45:10.1234567Z',
        enrollments,
        enrollmentsPrevious: stage === 'declining' ? 1_250 : enrollments,
        ...(enrolledEver ? { trendPercent: -47 } : {}),
        succeeded: Math.floor(enrollments * 0.9),
        failed: Math.floor(enrollments * 0.05),
        ...(enrollments > 0 ? { successPercent: 94, dailyEnrollments: Array.from({ length: 90 }, (_, d) => 10 + ((d * 7 + i) % 90)) } : {}),
        activeDays: enrollments > 0 ? 88 : 0,
        ...(enrolledEver ? { firstEnrollmentDay: '2026-05-21', lastActiveDay: '2026-11-05', lastCounterUpdateAt: '2026-11-06T07:12:45.1234567Z' } : {}),
        lifetimeEnrollments: enrolledEver ? 123_456 : 0,
        lifetimeSucceeded: enrolledEver ? 112_233 : 0,
        lifetimeEvents: enrolledEver ? 45_678_901 : 0,
        portalUsers: 12,
        lastPortalVisitAt: '2026-11-06T08:30:00.1234567Z',
      });
    }
  }
  const daily = Array.from({ length: 90 }, (_, d) => ({
    day: new Date(Date.UTC(2026, 7, 8 + d)).toISOString().slice(0, 10), enrollments: 1_234, succeeded: 1_100, failed: 80, activeTenants: 150,
  }));
  return response(tenants, { windowDays: 90, windowStart: '2026-08-08', previousWindowStart: '2026-05-10', daily });
}

describe('shapeTenantActivity — size', () => {
  it('every view and every stage of 1,000 signups stays under the response cap of the tool', () => {
    const data = platform();
    expect(data.tenants).toHaveLength(1_000);
    const fullRow = JSON.stringify(data.tenants.find((t) => t.stage === 'active')).length;
    expect(fullRow).toBeGreaterThan(500); // a realistic wire row, so the cut views have real work to do

    const calls: Array<[TenantActivityView, TenantActivityStage?]> = [
      ...TENANT_ACTIVITY_VIEWS.map((view): [TenantActivityView] => [view]),
      ...TENANT_ACTIVITY_STAGES.map((stage): [TenantActivityView, TenantActivityStage] => ['lifecycle', stage]),
    ];
    const oversized = calls
      .map(([view, stage]) => ({ call: `${view}${stage ? `+${stage}` : ''}`, chars: JSON.stringify(shapeTenantActivity(data, view, stage)).length }))
      .filter((s) => s.chars >= MAX_RESULT_SIZE_CHARS.adminStream);

    expect(oversized).toEqual([]);
    // The fixture is big enough to hit the caps it guards.
    expect(stagesOf(shapeTenantActivity(data, 'lifecycle', 'neverEnrolled')).neverEnrolled).toMatchObject({ total: 600, truncated: true });
    expect(stagesOf(shapeTenantActivity(data, 'lifecycle')).dormant).toMatchObject({ total: 150, truncated: true });
  });
});

// ── The tool ──────────────────────────────────────────────────────────────────

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;
interface Registered { def: { inputSchema: Record<string, any> }; handler: Handler }

function registered(ga: boolean, strictGa: boolean, delegated = false): Record<string, Registered> {
  const tools: Record<string, Registered> = {};
  const fake = { registerTool: (name: string, def: Registered['def'], handler: Handler) => { tools[name] = { def, handler }; } };
  registerAdminTools(fake as never, ga, strictGa, delegated);
  return tools;
}

describe('get_tenant_activity — registration', () => {
  it('is listed for Global Admins and Global Readers only', () => {
    expect(registered(true, true)).toHaveProperty('get_tenant_activity');
    expect(registered(true, false)).toHaveProperty('get_tenant_activity');
    expect(registered(false, false)).not.toHaveProperty('get_tenant_activity');
    expect(registered(false, false, true)).not.toHaveProperty('get_tenant_activity');
  });

  it('takes the stages from the generated vocabulary and the views from the shaper', () => {
    const { inputSchema } = registered(true, false).get_tenant_activity.def;

    expect(inputSchema.stage.unwrap().options).toEqual([...TENANT_ACTIVITY_STAGES]);
    expect(inputSchema.view.parse(undefined)).toBe('summary');
    for (const view of TENANT_ACTIVITY_VIEWS) expect(inputSchema.view.parse(view)).toBe(view);
    expect(inputSchema.days.safeParse(91).success).toBe(false);
  });
});

describe('get_tenant_activity — request', () => {
  beforeEach(() => apiFetchMock.mockReset());
  const handler = () => registered(true, true).get_tenant_activity.handler;

  it('sends days to the route and keeps view and stage in the MCP server', async () => {
    apiFetchMock.mockResolvedValueOnce(response([tenant(1, { stage: 'dormant', lifetimeEnrollments: 12 })]));

    const result = await handler()({ days: 7, view: 'lifecycle', stage: 'dormant' });

    expect(result.isError).toBeUndefined();
    expect(apiFetchMock.mock.calls[0][0]).toBe('/api/global/metrics/tenant-activity?days=7');
    const out = JSON.parse(result.content[0].text);
    expect(out.view).toBe('lifecycle');
    expect(Object.keys(out.stages)).toEqual(['dormant']);
  });

  it('leaves an unset window to the backend default', async () => {
    apiFetchMock.mockResolvedValueOnce(response([]));

    const result = await handler()({ view: 'summary' });

    expect(apiFetchMock.mock.calls[0][0]).toBe('/api/global/metrics/tenant-activity');
    expect(JSON.parse(result.content[0].text).view).toBe('summary');
  });

  it('refuses stage with another view than lifecycle, before any request', async () => {
    const result = await handler()({ view: 'ranking', stage: 'dormant' });

    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/stage applies to view=lifecycle only/);
    expect(apiFetchMock).not.toHaveBeenCalled();
  });
});
