/**
 * The alert-rule catalog narrowed to a search: every whitespace-separated term must occur,
 * case-insensitively, in "{category} {eventType}", so "feedback rating" finds FeedbackRatingReceived
 * and "consent" keeps the whole Consent group. Categories without a match drop out; an empty query
 * keeps everything. It narrows what is shown, never the rules that are saved.
 */
export function filterOpsEventCatalog(
  catalog: Readonly<Record<string, readonly string[]>>,
  query: string,
): [string, string[]][] {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  return Object.entries(catalog)
    .map(([category, eventTypes]): [string, string[]] => [
      category,
      eventTypes.filter((eventType) => {
        const haystack = `${category} ${eventType}`.toLowerCase();
        return terms.every((term) => haystack.includes(term));
      }),
    ])
    .filter(([, eventTypes]) => eventTypes.length > 0);
}
