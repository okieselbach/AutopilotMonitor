// Search syntax for the Event Timeline search box.
//
// Follows the convention every search box shares (Google, GitHub, Gmail, Jira):
// whitespace-separated terms are AND-ed, a leading minus turns a term into an
// exclusion. Both sides match the same fields — eventType, message, source and the
// values of the structured `data` payload (the JSON under the expanded event) — so
// `-x` is exactly the negation of searching for `x`, with no second rule to learn.
//
//   error                    events matching "error"
//   esp provisioning         events matching BOTH terms (each in any of the fields)
//   "Installation completed" a phrase — found even when it only appears in the details JSON
//   -app_install_progress    everything EXCEPT matches of that term
//   error -heartbeat         combined
//   "-1"                     quoted: a literal minus, not an exclusion
//   -"exit code 1"           excludes the whole phrase
//   type=app_install         restricted to the event type
//   data=hpia-log-collect    restricted to the details payload (alias: content=)
//   -source=ImeLogTracker    an exclusion restricted to one field
//
// Matching is case-insensitive substring, so a partial type name such as
// `-app_install` hides every app_install_* event. The tokenizer is shared with the
// session search (lib/searchQueryTokens); a qualifier that is not in
// EVENT_SEARCH_QUALIFIERS stays a literal term (`14:30`, `foo=bar`).
//
// The payload is searched by its VALUES only, never by its keys — otherwise `-error`
// would hide every event that merely carries an `errorCode: null` field. Nested JSON
// strings are parsed first, exactly as the details view does, so what the user reads
// in the expanded event is what a term can match.

import { literalText, tokenizeSearchQuery } from "@/lib/searchQueryTokens";
import { normalizeJsonLikeValue } from "./eventHelpers";

/** The fields a search term is matched against. Structural subset of EnrollmentEvent. */
export interface EventSearchFields {
  eventType?: string | null;
  message?: string | null;
  source?: string | null;
  data?: Record<string, unknown> | null;
}

export type EventSearchFieldKey = keyof EventSearchFields;

/** The word in front of `=` / `:` → the one field the term may match. Lowercase. */
export const EVENT_SEARCH_QUALIFIERS: Readonly<Record<string, EventSearchFieldKey>> = {
  type: "eventType",
  message: "message",
  source: "source",
  data: "data",
  content: "data",
};

export interface EventSearchTerm {
  /** Lowercased. */
  text: string;
  /** Set for a qualified term: the one field it may match. */
  field?: EventSearchFieldKey;
}

export interface ParsedEventSearchQuery {
  /** Terms that must all match. */
  include: EventSearchTerm[];
  /** Terms that must not match. */
  exclude: EventSearchTerm[];
}

export function parseEventSearchQuery(query: string): ParsedEventSearchQuery {
  const include: EventSearchTerm[] = [];
  const exclude: EventSearchTerm[] = [];

  for (const token of tokenizeSearchQuery(query)) {
    const field = token.qualifier === undefined ? undefined : EVENT_SEARCH_QUALIFIERS[token.qualifier.toLowerCase()];
    const term: EventSearchTerm = field
      ? { text: token.text.toLowerCase(), field }
      : { text: literalText(token).toLowerCase() };
    const target = token.negated ? exclude : include;
    if (!target.some(t => t.text === term.text && t.field === term.field)) target.push(term);
  }

  return { include, exclude };
}

/** How a term reads for the "hiding …" chip: `source=x` for a qualified term, `x` otherwise. */
export function formatEventSearchTerm(term: EventSearchTerm): string {
  if (!term.field) return term.text;
  const qualifier = Object.entries(EVENT_SEARCH_QUALIFIERS).find(([, key]) => key === term.field)?.[0] ?? term.field;
  return `${qualifier}=${term.text}`;
}

function collectLeafValues(value: unknown, out: string[]): void {
  if (value === null || value === undefined) return;
  if (Array.isArray(value)) {
    for (const item of value) collectLeafValues(item, out);
    return;
  }
  if (typeof value === "object") {
    for (const item of Object.values(value)) collectLeafValues(item, out);
    return;
  }
  out.push(String(value));
}

// The payload text is derived once per data object, not once per keystroke: the matcher
// is rebuilt on every query change but the event objects stay the same.
const dataTextCache = new WeakMap<object, string>();

/**
 * The searchable text of an event's payload: every leaf value, lowercased, newline-joined
 * so a term can never match across two values. Empty when there is no payload.
 */
export function eventDataSearchText(data: Record<string, unknown> | null | undefined): string {
  if (!data) return "";
  const cached = dataTextCache.get(data);
  if (cached !== undefined) return cached;

  const values: string[] = [];
  collectLeafValues(normalizeJsonLikeValue(data), values);
  const text = values.join("\n").toLowerCase();
  dataTextCache.set(data, text);
  return text;
}

function fieldText(event: EventSearchFields, field: EventSearchFieldKey): string {
  if (field === "data") return eventDataSearchText(event.data);
  return (event[field] ?? "").toLowerCase();
}

const ALL_FIELDS: readonly EventSearchFieldKey[] = ["eventType", "message", "source", "data"];

function termMatches(term: EventSearchTerm, event: EventSearchFields): boolean {
  if (term.field) return fieldText(event, term.field).includes(term.text);
  // Field by field, so a term can never match across a field boundary, while a
  // multi-term query is still free to satisfy its terms from different fields.
  return ALL_FIELDS.some(field => fieldText(event, field).includes(term.text));
}

/**
 * Predicate for the parsed query, or null when the query carries no filter at all
 * (empty, whitespace, a lone minus or a bare `type=` while the user is still typing) —
 * callers use null to skip filtering entirely rather than filtering with an always-true
 * predicate.
 */
export function buildEventSearchMatcher(
  query: string,
): ((event: EventSearchFields) => boolean) | null {
  const { include, exclude } = parseEventSearchQuery(query);
  if (include.length === 0 && exclude.length === 0) return null;

  return (event: EventSearchFields) =>
    include.every(t => termMatches(t, event)) && !exclude.some(t => termMatches(t, event));
}
