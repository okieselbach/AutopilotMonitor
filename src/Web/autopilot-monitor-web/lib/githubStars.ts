/**
 * GitHub star count for the public surface (navbar, testimonials, footer).
 *
 * The count is baked in at deploy time: deploy-web.yml reads it with the job token and passes
 * it to `next build` as NEXT_PUBLIC_GITHUB_STARS, which Next inlines into the static HTML.
 * Visitors' browsers never call GitHub — the CSP would block it, and it would be a third-party
 * connection the privacy page does not list. CI, local builds and a failed lookup have no
 * value; the star link then renders without the counter.
 */

export const GITHUB_REPO_URL = "https://github.com/okieselbach/AutopilotMonitor";

/** Parses the build-time env value. Anything but a plain non-negative integer yields null. */
export function parseStarCount(raw: string | undefined): number | null {
  const value = raw?.trim();
  if (!value || !/^\d+$/.test(value)) return null;
  const count = Number(value);
  return Number.isSafeInteger(count) ? count : null;
}

/**
 * Compact display: 45 → "45", 1234 → "1.2k", 12000 → "12k".
 * Truncates instead of rounding so the label never overstates the count.
 */
export function formatStarCount(count: number): string {
  if (count < 1000) return String(count);
  const thousands = Math.floor(count / 100) / 10;
  return `${thousands}k`;
}

export const GITHUB_STARS = parseStarCount(process.env.NEXT_PUBLIC_GITHUB_STARS);
