/**
 * Sliding-window Levenshtein: checks if any substring of `haystack`
 * (of roughly `needle` length) is within edit distance `maxDistance`
 * of the needle (case-insensitive).
 *
 * Mirrors the backend FuzzyContains algorithm in
 * TableStorageService.AgentApi.cs for consistent behaviour.
 */
export function fuzzyContains(
  haystack: string,
  needle: string,
  maxDistance: number,
): boolean {
  if (!haystack || !needle) return false;

  const h = haystack.toLowerCase();
  const n = needle.toLowerCase();
  const nLen = n.length;

  if (nLen > h.length + maxDistance) return false;

  let prev = Array.from({ length: nLen + 1 }, (_, j) => j);
  let curr = new Array<number>(nLen + 1);

  for (let i = 1; i <= h.length; i++) {
    curr[0] = 0; // allow matching to start at any position in haystack
    for (let j = 1; j <= nLen; j++) {
      const cost = h[i - 1] === n[j - 1] ? 0 : 1;
      curr[j] = Math.min(curr[j - 1] + 1, prev[j] + 1, prev[j - 1] + cost);
    }
    if (curr[nLen] <= maxDistance) return true;
    [prev, curr] = [curr, prev];
  }

  return false;
}
