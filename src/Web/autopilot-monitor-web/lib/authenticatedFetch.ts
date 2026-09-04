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

  // One id per logical call: the 401 retry below is the same call and carries the same id, so
  // both backend request rows join on it. Minted here, never read back (see correlationId.ts).
  const correlationId = newCorrelationId();
  const method = (init?.method ?? 'GET').toUpperCase();

  const headers = new Headers(init?.headers);
  headers.set('Authorization', `Bearer ${token}`);
  headers.set(CORRELATION_HEADER, correlationId);

  // Apply a default timeout unless the caller already provided an AbortSignal
  const signal = init?.signal ?? AbortSignal.timeout(DEFAULT_TIMEOUT_MS);

  const response = await fetch(url, { ...init, headers, signal });

  if (response.status === 401) {
    // Force MSAL to bypass its cache and obtain a fresh token.
    const freshToken = await getAccessToken(true);
    if (!freshToken) {
      throw new TokenExpiredError();
    }

    const retryHeaders = new Headers(init?.headers);
    retryHeaders.set('Authorization', `Bearer ${freshToken}`);
    retryHeaders.set(CORRELATION_HEADER, correlationId);

    const retrySignal = init?.signal ?? AbortSignal.timeout(DEFAULT_TIMEOUT_MS);
    const retryResponse = await fetch(url, { ...init, headers: retryHeaders, signal: retrySignal });
    if (retryResponse.status === 401) {
      throw new TokenExpiredError();
    }
    if (retryResponse.status >= 400) trackFailure(url, method, retryResponse.status, correlationId);
    return retryResponse;
  }

  if (response.status >= 400) trackFailure(url, method, response.status, correlationId);
  return response;
}
