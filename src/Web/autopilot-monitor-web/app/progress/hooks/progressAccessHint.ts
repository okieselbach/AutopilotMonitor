import type { ProgressTenantStatusResponse } from "@/lib/generated/wire-types.generated";

/** The note below the Progress Portal search for a member without a role. */
export type ProgressAccessHint =
  | { kind: "member" }
  | { kind: "unused"; signedUpOn: string | null };

/**
 * A member without a role always gets the hint; the tenant status only sharpens it. A pending or
 * failed status read (null) keeps the plain member hint, which needs no data.
 */
export function progressAccessHint(status: ProgressTenantStatusResponse | null): ProgressAccessHint {
  if (!status?.unused) return { kind: "member" };
  return { kind: "unused", signedUpOn: formatSignupDate(status.signedUpAt) };
}

/** "12 March 2026" (the docs' date style); null for a missing or unparsable date. */
export function formatSignupDate(iso: string | undefined): string | null {
  if (!iso) return null;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  return date.toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric", timeZone: "UTC" });
}
