// Search syntax for the Event Timeline search box.
//
// Follows the convention every search box shares (Google, GitHub, Gmail, Jira):
// whitespace-separated terms are AND-ed, a leading minus turns a term into an
// exclusion. Both sides match the same fields — eventType, message, source — so
// `-x` is exactly the negation of searching for `x`, with no second rule to learn.
//
//   error                    events matching "error"
//   esp provisioning         events matching BOTH terms (in any of the three fields)
//   -app_install_progress    everything EXCEPT matches of that term
//   error -heartbeat         combined
//   "-1"                     quoted: a literal minus, not an exclusion
//   -"exit code 1"           excludes the whole phrase
//
// Matching is case-insensitive substring, so a partial type name such as
// `-app_install` hides every app_install_* event.

/** The fields a search term is matched against. Structural subset of EnrollmentEvent. */
export interface EventSearchFields {
  eventType?: string | null;
  message?: string | null;
  source?: string | null;
}

export interface ParsedEventSearchQuery {
  /** Terms that must all match. Lowercased. */
  include: string[];
  /** Terms that must not match. Lowercased. */
  exclude: string[];
}

interface Token {
  text: string;
  negated: boolean;
}

// Hand-rolled so a quote can protect a leading minus: the negation is decided at
// the first character of a token, before any quote is consumed, which makes `"-1"`
// a literal search term while `-"foo bar"` excludes the phrase.
function tokenize(query: string): Token[] {
  const tokens: Token[] = [];
  let current: Token | null = null;
  let inQuotes = false;

  const flush = () => {
    if (current && current.text !== "") tokens.push(current);
    current = null;
  };

  for (const ch of query) {
    if (!inQuotes && /\s/.test(ch)) {
      flush();
      continue;
    }
    if (ch === '"') {
      current ??= { text: "", negated: false };
      inQuotes = !inQuotes;
      continue;
    }
    if (current === null) {
      // First character of a token: a bare minus opens an exclusion.
      if (ch === "-") {
        current = { text: "", negated: true };
        continue;
      }
      current = { text: "", negated: false };
    }
    current.text += ch;
  }
  flush();

  return tokens;
}

export function parseEventSearchQuery(query: string): ParsedEventSearchQuery {
  const include: string[] = [];
  const exclude: string[] = [];

  for (const token of tokenize(query)) {
    const text = token.text.toLowerCase();
    const target = token.negated ? exclude : include;
    if (!target.includes(text)) target.push(text);
  }

  return { include, exclude };
}

/**
 * Predicate for the parsed query, or null when the query carries no filter at all
 * (empty, whitespace, or a lone minus while the user is still typing) — callers use
 * null to skip filtering entirely rather than filtering with an always-true predicate.
 */
export function buildEventSearchMatcher(
  query: string,
): ((event: EventSearchFields) => boolean) | null {
  const { include, exclude } = parseEventSearchQuery(query);
  if (include.length === 0 && exclude.length === 0) return null;

  return (event: EventSearchFields) => {
    // Newline-joined so a term can never match across a field boundary, while a
    // multi-term query is still free to satisfy its terms from different fields.
    const haystack = `${event.eventType ?? ""}\n${event.message ?? ""}\n${event.source ?? ""}`.toLowerCase();
    return include.every(t => haystack.includes(t)) && !exclude.some(t => haystack.includes(t));
  };
}
