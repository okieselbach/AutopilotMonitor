/**
 * Authenticated fetch wrapper with automatic 401 retry.
 *
 * On a 401 response the wrapper forces an MSAL token refresh and retries the
 * request once.  If the retry also fails with 401, a `TokenExpiredError` is
 * thrown so callers can show a clear "session expired" message instead of a
 * generic backend error.
 *
 * All requests are subject to a default timeout (30s) to prevent hanging
 * indefinitely on unresponsive backends.  Callers can override the timeout
 * by passing their own AbortSignal via init.signal.
 */

import { trackEvent } from "./appInsights";
import { CORRELATION_HEADER, newCorrelationId } from "./correlationId";
import { takeSeed } from "./prefetchSeeds";
import { noteTenantConfigWritten, storedConfigHeaders, writesTenantConfig } from "./storedConfigHint";
import { viewAsHeaders } from "./viewAs";

/** Default request timeout in milliseconds */
const DEFAULT_TIMEOUT_MS = 30_000;

/**
 * Every failed API call (final status >= 400) lands in App Insights with its correlation id,
 * so a user report ("it said error") joins the backend request row by one KQL predicate.
 * Path only — query strings can carry ids and filters that are not telemetry material.
 */
function trackFailure(url: string, method: string, status: number, correlationId: string) {
  let path = url;
  try {
    path = new URL(url, "http://localhost").pathname;
  } catch {
    /* keep the raw url */
  }
  trackEvent("api_request_failed", { path, method, status, correlationId });
}

export class TokenExpiredError extends Error {
  constructor() {
    super('Your session has expired. Please reload the page to sign in again.');
    this.name = 'TokenExpiredError';
  }
}

type GetAccessToken = (forceRefresh?: boolean) => Promise<string | null>;

export async function authenticatedFetch(
  url: string,
  getAccessToken: GetAccessToken,
  init?: RequestInit,
): Promise<Response> {
  const token = await getAccessToken();
  if (!token) {
    throw new TokenExpiredError();
  }

  const method = (init?.method ?? 'GET').toUpperCase();

  // A response seed (lib/prefetchSeeds.ts) is the identical GET already started by the auth
  // bootstrap; it is handed to exactly one caller. A 401 seed means its token was stale —
  // fall through to the normal path, which refreshes the token.
  if (method === 'GET') {
    const seed = takeSeed(url);
    if (seed) {
      try {
        const seeded = await seed.response;
        if (seeded.status !== 401) {
          if (seeded.status >= 400) trackFailure(url, method, seeded.status, seed.correlationId);
          return seeded;
        }
      } catch {
        // Seed failed (network/timeout) — the fetch below reports its own outcome.
      }
    }
  }

  // One id per logical call: the 401 retry below is the same call and carries the same id, so
  // both backend request rows join on it. Minted here, never read back (see correlationId.ts).
  const correlationId = newCorrelationId();

  // A view-as tab (lib/viewAs.ts) names the viewed tenant on every call. It shares the browser's HTTP
  // cache with ordinary tabs and no response varies on those headers, so it bypasses that cache.
  // A browser that has just written a tenant configuration asks for the stored row
  // (lib/storedConfigHint.ts). That header leaves the cache mode alone: with "no-store" the browser
  // preflights every call again, and the API's answers carry no freshness lifetime to serve from.
  const viewAs = viewAsHeaders();
  const extraHeaders = { ...viewAs, ...storedConfigHeaders() };
  const cache = Object.keys(viewAs).length > 0 ? 'no-store' : init?.cache;
  const headersFor = (bearer: string): Headers => {
    const headers = new Headers(init?.headers);
    headers.set('Authorization', `Bearer ${bearer}`);
    headers.set(CORRELATION_HEADER, correlationId);
    for (const [name, value] of Object.entries(extraHeaders)) headers.set(name, value);
    return headers;
  };

  const settle = (final: Response): Response => {
    if (final.status >= 400) {
      trackFailure(url, method, final.status, correlationId);
    } else if (final.ok && writesTenantConfig(method, url)) {
      // From here on this browser's reads ask for the stored row: another instance may answer them.
      noteTenantConfigWritten();
    }
    return final;
  };

  // Apply a default timeout unless the caller already provided an AbortSignal
  const signal = init?.signal ?? AbortSignal.timeout(DEFAULT_TIMEOUT_MS);

  const response = await fetch(url, { ...init, headers: headersFor(token), signal, cache });

  if (response.status === 401) {
    // Force MSAL to bypass its cache and obtain a fresh token.
    const freshToken = await getAccessToken(true);
    if (!freshToken) {
      throw new TokenExpiredError();
    }

    const retrySignal = init?.signal ?? AbortSignal.timeout(DEFAULT_TIMEOUT_MS);
    const retryResponse = await fetch(url, { ...init, headers: headersFor(freshToken), signal: retrySignal, cache });
    if (retryResponse.status === 401) {
      throw new TokenExpiredError();
    }
    return settle(retryResponse);
  }

  return settle(response);
}
