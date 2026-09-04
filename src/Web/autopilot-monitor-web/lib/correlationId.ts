/**
 * Per-request correlation id for API calls.
 *
 * The backend's CorrelationIdMiddleware honours an inbound `X-Correlation-ID` when it matches
 * `^[A-Za-z0-9_-]{1,128}$`, echoes it as a response header, stamps it on the App Insights request
 * row (customDimensions.CorrelationId) and writes it into every error body (`correlationId`).
 * Minting the id HERE — not reading the server's — is deliberate: platform CORS exposes no
 * response headers to browser script, so a client-minted id is the only one both sides know.
 * A UUID satisfies the backend's allow-list.
 */
export const CORRELATION_HEADER = "X-Correlation-ID";

export function newCorrelationId(): string {
  const c = typeof globalThis !== "undefined" ? (globalThis as { crypto?: Crypto }).crypto : undefined;
  if (c && typeof c.randomUUID === "function") return c.randomUUID();
  if (c && typeof c.getRandomValues === "function") {
    const bytes = new Uint8Array(16);
    c.getRandomValues(bytes);
    return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  }
  // No Web Crypto (very old runtime): still unique enough to correlate one report.
  return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2, 14)}`;
}

/** Short form for user-facing "Ref" lines: the first block of a UUID, or the first 8 chars. */
export function shortCorrelationId(id: string): string {
  return id.split("-")[0].slice(0, 8);
}
