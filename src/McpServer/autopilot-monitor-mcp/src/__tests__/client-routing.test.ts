/**
 * Unit tests for the GA-gating / caller-context routing (review finding tests-6).
 *
 * pickGlobalOrTenantPath is the hinge of the MCP server's tenant isolation:
 * a Global Admin is routed to /api/global/* (cross-tenant), everyone else to
 * the tenant-scoped /api/* variant where the backend resolves the tenant from
 * the JWT. A regression that flips this default would either expose
 * cross-tenant endpoints to a tenant-admin or break GA cross-tenant queries.
 *
 * runWithCaller scopes that decision per async context so concurrent sessions
 * cannot observe each other's token / GA flag — this is asserted explicitly.
 */
import { describe, it, expect } from 'vitest';
import {
  pickGlobalOrTenantPath,
  isGlobalAdmin,
  hasGlobalScope,
  hasCrossTenantRouting,
  isDelegated,
  getDelegatedTenantIds,
  enforceDelegatedTenant,
  enforceDelegatedTenantForPage,
  getCurrentToken,
  runWithCaller,
} from '../client.js';

const GLOBAL = '/api/global/devices/blocked';
const TENANT = '/api/devices/blocked';

describe('pickGlobalOrTenantPath — GA gating', () => {
  it('defaults to the tenant path when no caller context is active', () => {
    // No runWithCaller wrapper ⇒ isGlobalAdmin() is false by design.
    expect(isGlobalAdmin()).toBe(false);
    expect(getCurrentToken()).toBeUndefined();
    expect(pickGlobalOrTenantPath(GLOBAL, TENANT)).toBe(TENANT);
  });

  it('routes a Global Admin to the /api/global/* path', () => {
    runWithCaller({ token: 'ga-token', isGlobalAdmin: true }, () => {
      expect(isGlobalAdmin()).toBe(true);
      expect(getCurrentToken()).toBe('ga-token');
      expect(pickGlobalOrTenantPath(GLOBAL, TENANT)).toBe(GLOBAL);
    });
  });

  it('routes a non-GA caller to the tenant-scoped path', () => {
    runWithCaller({ token: 'tenant-token', isGlobalAdmin: false }, () => {
      expect(isGlobalAdmin()).toBe(false);
      expect(pickGlobalOrTenantPath(GLOBAL, TENANT)).toBe(TENANT);
    });
  });

  it('routes a read-only Global Reader to /api/global/* (scope, not GA write status)', () => {
    runWithCaller({ token: 'reader-token', isGlobalAdmin: false, isGlobalReader: true }, () => {
      expect(isGlobalAdmin()).toBe(false);   // not a write-tier Global Admin
      expect(hasGlobalScope()).toBe(true);   // but has cross-tenant read scope
      expect(pickGlobalOrTenantPath(GLOBAL, TENANT)).toBe(GLOBAL);
    });
  });

  it('reverts to the no-context default after the callback returns', () => {
    runWithCaller({ token: 't', isGlobalAdmin: true }, () => {
      expect(isGlobalAdmin()).toBe(true);
    });
    // Context must not leak past the run() boundary.
    expect(isGlobalAdmin()).toBe(false);
    expect(getCurrentToken()).toBeUndefined();
  });
});

describe('delegated (MSP) routing + tenant enforcement', () => {
  const A = 'aaaa-1111';
  const B = 'bbbb-2222';

  it('routes a delegated caller to /api/global/* (cross-tenant routing) without platform scope', () => {
    runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [A] }, () => {
      expect(isGlobalAdmin()).toBe(false);
      expect(hasGlobalScope()).toBe(false);          // NOT platform scope (catalog gating stays closed)
      expect(isDelegated()).toBe(true);
      expect(hasCrossTenantRouting()).toBe(true);    // but DOES route cross-tenant
      expect(getDelegatedTenantIds()).toEqual([A]);
      expect(pickGlobalOrTenantPath(GLOBAL, TENANT)).toBe(GLOBAL);
    });
  });

  it('enforceDelegatedTenant returns the managed tenant (lowercased) for an in-scope id', () => {
    runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [A, B] }, () => {
      expect(enforceDelegatedTenant(A)).toBe(A);
      expect(enforceDelegatedTenant('AAAA-1111')).toBe(A); // case-insensitive
    });
  });

  it('enforceDelegatedTenant throws for an out-of-scope tenant', () => {
    runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [A] }, () => {
      expect(() => enforceDelegatedTenant(B)).toThrow(/Not authorized for tenant/);
    });
  });

  it('enforceDelegatedTenant throws when a delegated caller omits tenantId (no aggregate)', () => {
    runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [A] }, () => {
      expect(() => enforceDelegatedTenant(undefined)).toThrow(/tenantId is required/);
      expect(() => enforceDelegatedTenant('')).toThrow(/tenantId is required/);
    });
  });

  it('enforceDelegatedTenantForPage accepts a page-2 call carrying the tenant in the continuation only', () => {
    runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [A] }, () => {
      // Documented page-2 form: only a full backend nextLink, no explicit tenantId arg.
      const nextLink = `/api/global/raw/sessions?pageSize=200&continuation=abc&tenantId=${A}`;
      expect(enforceDelegatedTenantForPage(undefined, nextLink)).toBe(A);
      // Uppercased tenant in the link still resolves.
      expect(enforceDelegatedTenantForPage(undefined, `/api/x?tenantId=AAAA-1111`)).toBe(A);
    });
  });

  it('enforceDelegatedTenantForPage rejects a continuation pointing at an unmanaged tenant (defense in depth)', () => {
    runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [A] }, () => {
      expect(() => enforceDelegatedTenantForPage(undefined, `/api/x?tenantId=${B}`)).toThrow(/Not authorized/);
    });
  });

  it('enforceDelegatedTenantForPage still throws when neither arg nor continuation names a tenant', () => {
    runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [A] }, () => {
      // Offset-only continuation carries no tenantId → falls back to the (missing) explicit arg.
      expect(() => enforceDelegatedTenantForPage(undefined, 'inv-offset:100')).toThrow(/tenantId is required/);
      expect(() => enforceDelegatedTenantForPage(undefined, undefined)).toThrow(/tenantId is required/);
    });
  });

  it('enforceDelegatedTenantForPage is a no-op for a non-delegated caller', () => {
    runWithCaller({ token: 'ga', isGlobalAdmin: true }, () => {
      expect(enforceDelegatedTenantForPage(undefined, '/api/x?tenantId=anything')).toBeUndefined();
      expect(enforceDelegatedTenantForPage('explicit', undefined)).toBe('explicit');
    });
  });

  it('enforceDelegatedTenant is a no-op for a non-delegated caller (GA tenantId stays optional)', () => {
    runWithCaller({ token: 'ga', isGlobalAdmin: true }, () => {
      expect(isDelegated()).toBe(false);
      expect(enforceDelegatedTenant(undefined)).toBeUndefined();
      expect(enforceDelegatedTenant('any-tenant')).toBe('any-tenant');
      expect(pickGlobalOrTenantPath(GLOBAL, TENANT)).toBe(GLOBAL); // routing unchanged for GA
    });
  });

  it('a plain tenant user neither routes cross-tenant nor is treated as delegated', () => {
    runWithCaller({ token: 'tn', isGlobalAdmin: false }, () => {
      expect(isDelegated()).toBe(false);
      expect(hasCrossTenantRouting()).toBe(false);
      expect(pickGlobalOrTenantPath(GLOBAL, TENANT)).toBe(TENANT);
    });
  });
});

describe('runWithCaller — context isolation', () => {
  it('keeps concurrent contexts from bleeding into each other', async () => {
    // Interleave two async callers on the event loop; each must continue to see
    // its own token + GA flag despite the other running between awaits.
    const ga = runWithCaller({ token: 'ga', isGlobalAdmin: true }, async () => {
      await Promise.resolve();
      return { token: getCurrentToken(), ga: isGlobalAdmin(), path: pickGlobalOrTenantPath(GLOBAL, TENANT) };
    });
    const tenant = runWithCaller({ token: 'tn', isGlobalAdmin: false }, async () => {
      await Promise.resolve();
      return { token: getCurrentToken(), ga: isGlobalAdmin(), path: pickGlobalOrTenantPath(GLOBAL, TENANT) };
    });

    const [gaResult, tenantResult] = await Promise.all([ga, tenant]);

    expect(gaResult).toEqual({ token: 'ga', ga: true, path: GLOBAL });
    expect(tenantResult).toEqual({ token: 'tn', ga: false, path: TENANT });
  });

  it('nested contexts restore the parent context on exit', () => {
    runWithCaller({ token: 'outer', isGlobalAdmin: true }, () => {
      expect(getCurrentToken()).toBe('outer');
      runWithCaller({ token: 'inner', isGlobalAdmin: false }, () => {
        expect(getCurrentToken()).toBe('inner');
        expect(isGlobalAdmin()).toBe(false);
      });
      // Inner run() must not clobber the outer context.
      expect(getCurrentToken()).toBe('outer');
      expect(isGlobalAdmin()).toBe(true);
    });
  });
});
