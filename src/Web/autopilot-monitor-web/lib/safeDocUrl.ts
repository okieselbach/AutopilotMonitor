/**
 * Returns the URL when it parses as an absolute http(s) URL, otherwise null.
 *
 * Rule relatedDocs URLs are author-controlled (Tenant Admin) and rendered as
 * anchor hrefs for every viewer, including cross-tenant admins. React does not
 * sanitize href, so a stored `javascript:` / `data:` URL would execute in the
 * portal origin on click. Every render site must go through this guard and
 * fall back to plain text for anything that is not http(s).
 */
export function safeHttpUrl(url: string | null | undefined): string | null {
  if (typeof url !== "string") return null;
  const trimmed = url.trim();
  if (!trimmed) return null;
  try {
    const parsed = new URL(trimmed);
    return parsed.protocol === "http:" || parsed.protocol === "https:" ? trimmed : null;
  } catch {
    return null;
  }
}
