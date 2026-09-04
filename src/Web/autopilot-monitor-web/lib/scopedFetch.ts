import { authenticatedFetch, TokenExpiredError } from "./authenticatedFetch";
import { shortCorrelationId } from "./correlationId";

/**
 * JSON fetch layer for the scope-migrated pages: authenticatedFetch + ok-check + body
 * parse in one call, so call sites stop hand-rolling the response boilerplate. Throws
 * ApiError on a non-ok response carrying the backend's error envelope
 * (`{ error, code, correlationId, hint? }` — every non-2xx body since the 2026-09 envelope
 * pass; TokenExpiredError from authenticatedFetch passes through untouched).
 */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    /** Machine-readable code (Constants.ApiErrorCodes or a domain code class); "" when the body had none. */
    public readonly code: string = "",
    /** The request's correlation id — the handle for the backend log; "" when the body had none. */
    public readonly correlationId: string = "",
    public readonly hint: string | null = null,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** Shape of the backend error envelope (ApiErrorResponse and its specialised siblings). */
interface ErrorEnvelope {
  error?: unknown;
  code?: unknown;
  correlationId?: unknown;
  hint?: unknown;
  /** Pre-envelope bodies used `message`; still read for the deploy window between backend and web. */
  message?: unknown;
}

const str = (v: unknown): string | null => (typeof v === "string" && v.length > 0 ? v : null);

/** Build an ApiError from a non-ok response: envelope fields when present, statusText otherwise. */
export async function apiErrorFromResponse(response: Response): Promise<ApiError> {
  let body: ErrorEnvelope | null = null;
  try {
    body = (await response.json()) as ErrorEnvelope;
  } catch {
    /* not JSON */
  }
  const message = str(body?.error) ?? str(body?.message) ?? response.statusText;
  return new ApiError(response.status, message, str(body?.code) ?? "", str(body?.correlationId) ?? "", str(body?.hint));
}

export async function fetchJson<T>(
  url: string,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  init?: RequestInit,
): Promise<T> {
  const response = await authenticatedFetch(url, getAccessToken, init);
  if (!response.ok) {
    throw await apiErrorFromResponse(response);
  }
  return (await response.json()) as T;
}

/**
 * One rendering for any thrown error: the user-facing message plus, for backend failures,
 * the short correlation id as a reference the user can quote to support. Pass `reference`
 * to addNotification so the bell shows it.
 */
export function describeApiError(err: unknown, fallback = "Request failed."): { message: string; reference: string | null } {
  if (err instanceof TokenExpiredError) return { message: err.message, reference: null };
  if (err instanceof ApiError) {
    return { message: err.message || fallback, reference: err.correlationId ? shortCorrelationId(err.correlationId) : null };
  }
  if (err instanceof Error && err.message) return { message: err.message, reference: null };
  return { message: fallback, reference: null };
}
