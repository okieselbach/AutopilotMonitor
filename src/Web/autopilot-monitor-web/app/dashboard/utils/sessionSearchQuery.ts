// Search syntax for the session list's search box.
//
// Same grammar as the event timeline (lib/searchQueryTokens): whitespace-separated
// terms are AND-ed, a leading minus excludes, quotes protect a phrase — plus field
// qualifiers, because "Contoso" and "EliteBook 840" live in two different columns and
// a phrase can never match across a field boundary:
//
//   surface                         substring in any searched field
//   surface failed                  both terms, each in any field
//   model=surface                   only the model field
//   manufacturer="Contoso Ltd"      phrase, only the manufacturer field
//   model="EliteBook 840" -failed   combined with an exclusion
//   "a=b"                           quoted: a literal, not a qualifier
//
// A qualifier that is not in SESSION_SEARCH_FIELDS stays a literal term (`14:30`,
// `foo=bar`). Qualified terms are substring matches like every other term — exact
// matching is the column filter's job. The backend parses the SAME grammar for the
// server-side sweep (`q=` on /api/search/sessions, SessionSearchQuery.cs); both sides
// run utils/session-search-syntax.cases.json, so a term that matches here matches there.

import type { Session } from "../types";
import { literalText, tokenizeSearchQuery } from "@/lib/searchQueryTokens";

export interface SessionSearchField {
  /** The word in front of `=` — lowercase, one per field. */
  readonly qualifier: string;
  readonly key: keyof Session;
  /** Column label, also shown as "Matched: …" in the suggestion list. */
  readonly label: string;
}

/**
 * The searched fields, in suggestion priority order. Exactly the set the backend's
 * free-text predicate searches — a field only one side knows would produce ghost
 * results (server finds it, client filters it back out) or the reverse.
 */
export const SESSION_SEARCH_FIELDS = [
  { qualifier: "device", key: "deviceName", label: "Device" },
  { qualifier: "serial", key: "serialNumber", label: "Serial" },
  { qualifier: "model", key: "model", label: "Model" },
  { qualifier: "manufacturer", key: "manufacturer", label: "Manufacturer" },
  { qualifier: "status", key: "status", label: "Status" },
  { qualifier: "session", key: "sessionId", label: "Session ID" },
  { qualifier: "country", key: "geoCountry", label: "Country" },
  { qualifier: "region", key: "geoRegion", label: "Region" },
  { qualifier: "city", key: "geoCity", label: "City" },
  { qualifier: "agent", key: "agentVersion", label: "Agent Version" },
  { qualifier: "os", key: "osName", label: "OS Name" },
  { qualifier: "build", key: "osBuild", label: "OS Build" },
  { qualifier: "osversion", key: "osDisplayVersion", label: "OS Version" },
  { qualifier: "edition", key: "osEdition", label: "OS Edition" },
  { qualifier: "language", key: "osLanguage", label: "OS Language" },
] as const satisfies readonly SessionSearchField[];

export type SessionSearchQualifier = (typeof SESSION_SEARCH_FIELDS)[number]["qualifier"];
export type SessionSearchKey = (typeof SESSION_SEARCH_FIELDS)[number]["key"];

/** The slice of a session the matcher reads. */
export type SearchableSession = Pick<Session, SessionSearchKey>;

export interface SessionSearchTerm {
  /** Lowercased. */
  text: string;
  /** Set for a qualified term: the one field it may match. */
  field?: SessionSearchKey;
}

export interface ParsedSessionSearchQuery {
  include: SessionSearchTerm[];
  exclude: SessionSearchTerm[];
}

const KEY_BY_QUALIFIER: ReadonlyMap<string, SessionSearchKey> = new Map(
  SESSION_SEARCH_FIELDS.map((f) => [f.qualifier, f.key]),
);

export function parseSessionSearchQuery(query: string): ParsedSessionSearchQuery {
  const include: SessionSearchTerm[] = [];
  const exclude: SessionSearchTerm[] = [];

  for (const token of tokenizeSearchQuery(query)) {
    const field = token.qualifier === undefined ? undefined : KEY_BY_QUALIFIER.get(token.qualifier.toLowerCase());
    const term: SessionSearchTerm = field
      ? { text: token.text.toLowerCase(), field }
      : { text: literalText(token).toLowerCase() };
    const target = token.negated ? exclude : include;
    if (!target.some((t) => t.text === term.text && t.field === term.field)) target.push(term);
  }

  return { include, exclude };
}

function fieldText(session: SearchableSession, key: SessionSearchKey): string {
  return String(session[key] ?? "").toLowerCase();
}

/**
 * Whether one term hits the session. `extra` is client-derived text (formatted date,
 * "N min", "blocked", tenant domain) that only a free term may match — newline-joined by
 * the caller, so a term can never span two of those tokens either.
 */
function termMatches(term: SessionSearchTerm, session: SearchableSession, extra?: string): boolean {
  if (term.field) return fieldText(session, term.field).includes(term.text);
  return (
    SESSION_SEARCH_FIELDS.some((f) => fieldText(session, f.key).includes(term.text)) ||
    (extra !== undefined && extra.toLowerCase().includes(term.text))
  );
}

/**
 * Predicate for the query, or null when it carries no filter (empty, whitespace, a
 * lone minus or a bare `model=` while the user is still typing).
 */
export function buildSessionSearchMatcher(
  query: string,
): ((session: SearchableSession, extra?: string) => boolean) | null {
  const { include, exclude } = parseSessionSearchQuery(query);
  if (include.length === 0 && exclude.length === 0) return null;

  return (session, extra) =>
    include.every((t) => termMatches(t, session, extra)) &&
    !exclude.some((t) => termMatches(t, session, extra));
}

/**
 * For the suggestion list: the first searched field an include term hits, qualified
 * terms first (they name the field the user meant). Null when only `extra` matched
 * (the caller labels that itself) or when the query has no include terms.
 */
export function matchedSearchField(
  parsed: ParsedSessionSearchQuery,
  session: SearchableSession,
): { label: string; value: string } | null {
  const ordered = [...parsed.include.filter((t) => t.field), ...parsed.include.filter((t) => !t.field)];
  for (const term of ordered) {
    for (const f of SESSION_SEARCH_FIELDS) {
      if (term.field && term.field !== f.key) continue;
      const value = session[f.key];
      if (value != null && String(value).toLowerCase().includes(term.text)) {
        return { label: f.label, value: String(value) };
      }
    }
  }
  return null;
}

/**
 * Builds a query of qualified terms in the caller's order — the deep link from Fleet
 * Health's model rows. Empty values are skipped; a value with whitespace or a grammar
 * character is quoted (double quotes inside it are dropped: the grammar has no escape).
 */
export function buildSessionSearchQuery(
  parts: Partial<Record<SessionSearchQualifier, string | null | undefined>>,
): string {
  const terms: string[] = [];
  for (const [qualifier, raw] of Object.entries(parts)) {
    const value = raw?.trim().replace(/"/g, "");
    if (!value || !KEY_BY_QUALIFIER.has(qualifier)) continue;
    terms.push(`${qualifier}=${/[\s=:]/.test(value) ? `"${value}"` : value}`);
  }
  return terms.join(" ");
}
