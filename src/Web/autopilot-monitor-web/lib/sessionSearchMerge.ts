/**
 * Merge helper for server-side session search results (Fragilitätsaudit P6.2).
 *
 * Search results arrive out-of-order relative to the loaded (newest-first) list and may
 * overlap with already-loaded or SignalR-updated sessions. The merge keeps every existing
 * item untouched (the loaded copy may be fresher — live updates mutate it) and appends only
 * genuinely new sessions; the dashboard's existing sort chain re-orders afterwards.
 *
 * Returns the ORIGINAL array reference when nothing new arrived so React state consumers
 * skip a no-op re-render.
 */
export function mergeSessionsById<T extends { sessionId: string }>(
  existing: T[],
  incoming: T[],
): T[] {
  if (incoming.length === 0) return existing;

  const known = new Set(existing.map((s) => s.sessionId));
  const fresh = incoming.filter((s) => !known.has(s.sessionId));
  if (fresh.length === 0) return existing;

  return [...existing, ...fresh];
}
