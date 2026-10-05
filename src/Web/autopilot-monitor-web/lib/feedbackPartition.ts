/**
 * Pure helpers for partitioning + paginating the mixed feedback list returned by
 * GET /api/feedback/all (General, In-App and Offboarding rows in one array).
 *
 * Extracted so vitest can pin the discriminator behaviour without booting React;
 * the FeedbackSection component just plumbs results through.
 */

export interface FeedbackEntryLike {
  type: "General" | "InApp" | "Offboarding" | string;
  upn: string;
  tenantId: string;
  rating: number | null;
  comment: string | null;
  dismissed: boolean;
  submitted: boolean;
  interactedAt: string | null;
  historyRowKey: string | null;
  domainName: string | null;
  feedbackId?: string | null;
  contactEmail?: string | null;
}

export type FeedbackTab = "General" | "InApp" | "Offboarding";

/**
 * Splits the mixed list into the three tab partitions. Unknown discriminator values
 * (future-proofing — server might add another kind) are dropped so they don't end up
 * misrendered in one of the existing tabs.
 */
export function partitionFeedback<T extends { type?: string | null }>(entries: readonly T[]): {
  general: T[];
  inApp: T[];
  offboarding: T[];
} {
  const general: T[] = [];
  const inApp: T[] = [];
  const offboarding: T[] = [];
  for (const e of entries) {
    if (e.type === "General") general.push(e);
    else if (e.type === "InApp") inApp.push(e);
    else if (e.type === "Offboarding") offboarding.push(e);
    // else: silently drop — future-proof against unknown kinds.
  }
  return { general, inApp, offboarding };
}

/**
 * Computes the slice + total-page metadata for the active tab. Returns 0 totalPages
 * when the page list is empty so the pagination UI hides itself.
 */
export function paginate<T>(entries: readonly T[], page: number, perPage: number): {
  items: T[];
  totalPages: number;
} {
  if (perPage <= 0) return { items: [], totalPages: 0 };
  const totalPages = Math.ceil(entries.length / perPage);
  const start = page * perPage;
  return {
    items: entries.slice(start, start + perPage),
    totalPages,
  };
}

/**
 * Computes the average rating across submitted In-App entries. Returns "—" when no
 * ratings exist (matches the legacy UI string).
 */
export function avgRating<T extends { submitted?: boolean; rating?: number | null }>(
  inAppEntries: readonly T[],
): string {
  const submitted = inAppEntries.filter(e => e.submitted && typeof e.rating === "number");
  if (submitted.length === 0) return "—";
  const sum = submitted.reduce((acc, e) => acc + (e.rating ?? 0), 0);
  return (sum / submitted.length).toFixed(1);
}
