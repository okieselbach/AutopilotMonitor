import { authenticatedFetch, TokenExpiredError } from "./authenticatedFetch";
import { shortCorrelationId } from "./correlationId";
import type { ApiErrorResponse } from "@/utils/wire-types.generated";

/**
 * The one API call layer above authenticatedFetch: ok-check, body parse and the backend's
 * error envelope in one place, so no call site hand-rolls `if (!res.ok)`, `.json()` or an
 * `instanceof TokenExpiredError` branch (guarded by lib/__tests__/apiClient.guard.test.ts).
 *
 *   fetchJson<T>  — JSON body expected; throws ApiError on non-2xx, empty or malformed body
 *   fetchOk       — action calls whose body nobody reads; returns the ok Response (status only)
 *   fetchBlob     — downloads
 *
 * Every non-2xx body is the typed envelope `{ error, code, correlationId, hint?,
 * retryAfterSeconds? }` (D-192); TokenExpiredError from authenticatedFetch passes through
 * untouched and is rendered by describeApiError like any other failure.
 */

export type GetAccessToken = (forceRefresh?: boolean) => Promise<string | null>;

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    /** Machine-readable code (Constants.ApiErrorCodes or a domain code class); "" when the body had none. */
    public readonly code: string = "",
    /** The request's correlation id — the handle for the backend log; "" when the body had none. */
    public readonly correlationId: string = "",
    public readonly hint: string | null = null,
    /** Seconds the server asked us to wait (429/503); null when it sent none. */
    public readonly retryAfterSeconds: number | null = null,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/**
 * What a non-2xx body may carry: the generated envelope plus `message`, which pre-envelope
 * backends wrote. The `message` fallback is a deploy-window remnant (backend 1.5.1171 shipped the
 * envelope on 2026-09-05); drop it once no older backend can answer this web build.
 */
type ErrorEnvelope = Partial<ApiErrorResponse> & { message?: unknown };

const str = (v: unknown): string | null => (typeof v === "string" && v.length > 0 ? v : null);
const num = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);

function retryAfterFromHeader(response: Response): number | null {
  // Platform CORS may not expose the header to browser script; the body's retryAfterSeconds
  // is the primary source, this is the best-effort second one.
  const raw = response.headers?.get?.("Retry-After");
  if (!raw) return null;
  const seconds = Number.parseInt(raw, 10);
  return Number.isFinite(seconds) && seconds >= 0 ? seconds : null;
}

/** Build an ApiError from a non-ok response: envelope fields when present, statusText otherwise. */
export async function apiErrorFromResponse(response: Response): Promise<ApiError> {
  let body: ErrorEnvelope | null = null;
  try {
    const text = await response.text();
    if (text.length > 0) body = JSON.parse(text) as ErrorEnvelope;
  } catch {
    /* not JSON */
  }
  const message = str(body?.error) ?? str(body?.message) ?? response.statusText;
  return new ApiError(
    response.status,
    message,
    str(body?.code) ?? "",
    str(body?.correlationId) ?? "",
    str(body?.hint),
    num(body?.retryAfterSeconds) ?? retryAfterFromHeader(response),
  );
}

/** A string body without an explicit Content-Type is JSON — every backend route deserialises JSON. */
function withJsonContentType(init: RequestInit | undefined): RequestInit | undefined {
  if (typeof init?.body !== "string") return init;
  const headers = new Headers(init.headers);
  if (headers.has("Content-Type")) return init;
  headers.set("Content-Type", "application/json");
  return { ...init, headers };
}

/** Parse the JSON body of an ok response; an empty or malformed body is an ApiError, never `undefined`. */
export async function parseJsonBody<T>(response: Response): Promise<T> {
  const text = await response.text();
  if (text.length === 0) throw new ApiError(response.status, "Empty response body");
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new ApiError(response.status, "Malformed response body");
  }
}

export async function fetchJson<T>(url: string, getAccessToken: GetAccessToken, init?: RequestInit): Promise<T> {
  const response = await authenticatedFetch(url, getAccessToken, withJsonContentType(init));
  if (!response.ok) throw await apiErrorFromResponse(response);
  return parseJsonBody<T>(response);
}

/** For calls whose body nobody reads: resolves with the ok Response (for status checks), throws ApiError otherwise. */
export async function fetchOk(url: string, getAccessToken: GetAccessToken, init?: RequestInit): Promise<Response> {
  const response = await authenticatedFetch(url, getAccessToken, withJsonContentType(init));
  if (!response.ok) throw await apiErrorFromResponse(response);
  return response;
}

export async function fetchBlob(url: string, getAccessToken: GetAccessToken, init?: RequestInit): Promise<Blob> {
  const response = await authenticatedFetch(url, getAccessToken, init);
  if (!response.ok) throw await apiErrorFromResponse(response);
  return response.blob();
}

function retryAdvice(err: ApiError): string {
  return err.retryAfterSeconds !== null && err.retryAfterSeconds > 0
    ? `Try again in ${err.retryAfterSeconds} s.`
    : "Try again shortly.";
}

/**
 * One rendering for any thrown error: the user-facing message plus, for backend failures,
 * the short correlation id as a reference the user can quote to support. 429 and 503 add the
 * server's retry advice. Pass `reference` to addNotification so the bell shows it.
 */
export function describeApiError(err: unknown, fallback = "Request failed."): { message: string; reference: string | null } {
  if (err instanceof TokenExpiredError) return { message: err.message, reference: null };
  if (err instanceof ApiError) {
    let message = err.message || fallback;
    if (err.status === 429) message = `${err.message || "Too many requests."} ${retryAdvice(err)}`;
    else if (err.status === 503) message = `${err.message || "Service temporarily unavailable."} ${retryAdvice(err)}`;
    return { message, reference: err.correlationId ? shortCorrelationId(err.correlationId) : null };
  }
  if (err instanceof Error && err.message) return { message: err.message, reference: null };
  return { message: fallback, reference: null };
}

/** describeApiError as one string for inline error slots (`setError`): "message (Ref abcd1234)". */
export function apiErrorText(err: unknown, fallback = "Request failed."): string {
  const { message, reference } = describeApiError(err, fallback);
  return reference ? `${message} (Ref ${reference})` : message;
}

export interface ApiErrorNotification {
  type: "error";
  title: string;
  message: string;
  key: string | undefined;
  reference: string | undefined;
}

/**
 * The notification for a failed call. A token expiry always renders as "Session Expired" under
 * one dedup key, whatever the caller's title — that is the one dialect every catch block used to
 * hand-write.
 */
export function apiErrorNotification(title: string, err: unknown, key?: string, fallback?: string): ApiErrorNotification {
  const { message, reference } = describeApiError(err, fallback);
  if (err instanceof TokenExpiredError) {
    return { type: "error", title: "Session Expired", message, key: "session-expired", reference: undefined };
  }
  return { type: "error", title, message, key, reference: reference ?? undefined };
}
