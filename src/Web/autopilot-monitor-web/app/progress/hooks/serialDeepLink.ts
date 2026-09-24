/**
 * Deep link into the progress page: `/progress?serial=<serial or device name>`.
 *
 * Organisations send this link to end users (onboarding mail, ticket, workflow) so they
 * can follow their device without typing the serial. It grants nothing the form does not:
 * sign-in stays mandatory, the tenant comes from the token, and roleless callers still need
 * the exact serial or device name server-side. A query parameter, not a fragment, because
 * ProtectedRoute keeps only `pathname + search` across the login redirect.
 */

export const SERIAL_PARAM = "serial";

/** Upper bound for a linked term; real serials and device names stay far below it. */
export const MAX_SERIAL_PARAM_LENGTH = 128;

/** The linked search term from a `location.search` string, or null when absent or unusable. */
export function readSerialParam(search: string): string | null {
  const value = new URLSearchParams(search).get(SERIAL_PARAM)?.trim();
  if (!value || value.length > MAX_SERIAL_PARAM_LENGTH) return null;
  return value;
}

/** `location.search` with the serial parameter set to `serial`, other parameters kept. */
export function withSerialParam(search: string, serial: string): string {
  const params = new URLSearchParams(search);
  params.set(SERIAL_PARAM, serial);
  return `?${params.toString()}`;
}
