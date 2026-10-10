/**
 * Centralised input-validation helpers.
 *
 * Every value that reaches an API call or is interpolated into a URL must
 * pass through one of these guards first.  Keep validators here so pages
 * don't roll their own regex and we have a single place to harden.
 */

// ---------------------------------------------------------------------------
// GUID / UUID
// ---------------------------------------------------------------------------

const GUID_RE =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Returns `true` when `value` is a well-formed GUID / UUID (any version). */
export function isGuid(value: string | null | undefined): boolean {
  return !!value && GUID_RE.test(value.trim());
}

/**
 * Returns the trimmed GUID if valid, otherwise `undefined`.
 * Handy for optional query-parameter slots: pass the result straight to the
 * URL builder and it will be omitted when invalid.
 */
export function asGuidOrUndefined(
  value: string | null | undefined,
): string | undefined {
  if (!value) return undefined;
  const trimmed = value.trim();
  return GUID_RE.test(trimmed) ? trimmed : undefined;
}

// ---------------------------------------------------------------------------
// Session ID (same shape as GUID today, but named separately for clarity)
// ---------------------------------------------------------------------------

export function isSessionId(value: string | null | undefined): boolean {
  return isGuid(value);
}
