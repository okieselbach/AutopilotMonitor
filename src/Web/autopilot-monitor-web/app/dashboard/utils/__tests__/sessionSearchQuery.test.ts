import { describe, expect, it } from "vitest";
import cases from "@/utils/session-search-syntax.cases.json";
import {
  SESSION_SEARCH_FIELDS,
  buildSessionSearchMatcher,
  buildSessionSearchQuery,
  matchedSearchField,
  parseSessionSearchQuery,
  type SearchableSession,
} from "../sessionSearchQuery";

const sample = cases.session as SearchableSession;

describe("session search grammar — shared parity cases", () => {
  it.each(cases.cases.map((c) => [c.query, c.expected, c.note, c.extra] as const))(
    "%j → %s (%s)",
    (query, expected, _note, extra) => {
      const matcher = buildSessionSearchMatcher(query);
      expect(matcher ? matcher(sample, extra) : true).toBe(expected);
    },
  );

  it("names every searched field exactly once, so the case file covers the vocabulary", () => {
    const qualifiers = SESSION_SEARCH_FIELDS.map((f) => f.qualifier);
    expect(new Set(qualifiers).size).toBe(qualifiers.length);
    expect(new Set(SESSION_SEARCH_FIELDS.map((f) => f.key)).size).toBe(qualifiers.length);
    // Every qualifier appears in at least one case, so a renamed field breaks the file.
    const text = cases.cases.map((c) => c.query.toLowerCase()).join("\n");
    for (const q of qualifiers) expect(text, `no case uses ${q}=`).toMatch(new RegExp(`${q}[=:]`));
    // The sample session carries a value for every searched field.
    for (const f of SESSION_SEARCH_FIELDS) expect(sample[f.key], f.key).toBeTruthy();
  });
});

describe("parseSessionSearchQuery", () => {
  it("resolves known qualifiers to session keys and keeps unknown ones literal", () => {
    expect(parseSessionSearchQuery('Model=Surface -status:failed 14:30 foo=bar')).toEqual({
      include: [{ text: "surface", field: "model" }, { text: "14:30" }, { text: "foo=bar" }],
      exclude: [{ text: "failed", field: "status" }],
    });
  });

  it("de-duplicates terms by text and field", () => {
    expect(parseSessionSearchQuery("x model=x model=x x")).toEqual({
      include: [{ text: "x" }, { text: "x", field: "model" }],
      exclude: [],
    });
  });
});

describe("buildSessionSearchMatcher", () => {
  it("returns null when nothing filters", () => {
    expect(buildSessionSearchMatcher("")).toBeNull();
    expect(buildSessionSearchMatcher(" - model= ")).toBeNull();
  });

  it("treats a missing field value as empty", () => {
    const matcher = buildSessionSearchMatcher("model=x")!;
    expect(matcher({ ...sample, model: undefined as unknown as string })).toBe(false);
    expect(buildSessionSearchMatcher("-model=x")!({ ...sample, model: undefined as unknown as string })).toBe(true);
  });
});

describe("matchedSearchField", () => {
  it("prefers the field a qualifier names over an earlier free hit", () => {
    // "e" is in the device name (first field) but the qualifier says manufacturer.
    expect(matchedSearchField(parseSessionSearchQuery("e manufacturer=contoso"), sample)).toEqual({
      label: "Manufacturer",
      value: "Contoso",
    });
  });

  it("reports the first field in priority order for a free term", () => {
    expect(matchedSearchField(parseSessionSearchQuery("2222"), sample)).toEqual({
      label: "Session ID",
      value: sample.sessionId,
    });
  });

  it("returns null when only client-derived text could have matched", () => {
    expect(matchedSearchField(parseSessionSearchQuery("blocked"), sample)).toBeNull();
    expect(matchedSearchField(parseSessionSearchQuery("-failed"), sample)).toBeNull();
  });
});

describe("buildSessionSearchQuery", () => {
  it("quotes values with spaces or grammar characters and skips empty ones", () => {
    expect(buildSessionSearchQuery({ manufacturer: "Contoso", model: "EliteBook 840" })).toBe(
      'manufacturer=Contoso model="EliteBook 840"',
    );
    expect(buildSessionSearchQuery({ model: "X=1", manufacturer: "  " })).toBe('model="X=1"');
    expect(buildSessionSearchQuery({ model: "B", manufacturer: "A" })).toBe("model=B manufacturer=A");
    expect(buildSessionSearchQuery({ manufacturer: null, model: undefined })).toBe("");
  });

  it("drops double quotes — the grammar has no escape", () => {
    expect(buildSessionSearchQuery({ model: 'Tablet 10"' })).toBe('model="Tablet 10"');
  });

  it("round-trips through the parser", () => {
    const query = buildSessionSearchQuery({ manufacturer: "Contoso", model: "EliteBook 840" });
    expect(buildSessionSearchMatcher(query)!(sample)).toBe(true);
    expect(buildSessionSearchMatcher(query)!({ ...sample, manufacturer: "Fabrikam" })).toBe(false);
  });
});
