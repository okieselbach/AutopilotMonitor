import { authenticatedFetch } from "./authenticatedFetch";

/**
 * JSON fetch layer for the scope-migrated pages: authenticatedFetch + ok-check + body
 * parse in one call, so call sites stop hand-rolling the response boilerplate. Throws
 * ApiError on a non-ok response with the backend's `message` when the body carries one
 * (TokenExpiredError from authenticatedFetch passes through untouched).
 */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export async function fetchJson<T>(
  url: string,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  init?: RequestInit,
): Promise<T> {
  const response = await authenticatedFetch(url, getAccessToken, init);
  if (!response.ok) {
    let detail = response.statusText;
    try {
      const body = await response.json();
      if (body?.message) detail = body.message;
    } catch {
      /* not JSON */
    }
    throw new ApiError(response.status, detail);
  }
  return (await response.json()) as T;
}
