// Tokenizer shared by the portal's search boxes (session list, event timeline).
//
// One grammar, so a user learns it once:
//   whitespace separates terms          esp apps          → two terms
//   "…" protects spaces and specials    "exit code -1"    → one literal term
//   a leading minus excludes            -heartbeat        → negated term
//   key=value / key:value qualifies     model=surface     → term restricted to one field
//
// The tokenizer only splits; it never decides whether a qualifier is a real field —
// that is the consumer's vocabulary (`literalText` glues an unknown one back together,
// so `14:30` or `a=b` stay ordinary search terms). A qualifier is recognised only when
// the separator comes before any quote in the token: `"model"=x` and `"a=b"` are literals.
// There is no escape character; a double quote can not itself be searched.

export interface SearchToken {
  /** Term text with quotes removed, original case. Empty text is never emitted. */
  text: string;
  /** True when the token started with an unquoted minus. */
  negated: boolean;
  /** Field name in front of the first unquoted `=` / `:`, when there was one. */
  qualifier?: string;
  /** The separator that introduced `qualifier` (kept so an unknown key can be restored). */
  separator?: "=" | ":";
}

interface Draft extends SearchToken {
  sawQuote: boolean;
}

export function tokenizeSearchQuery(query: string): SearchToken[] {
  const tokens: SearchToken[] = [];
  let current: Draft | null = null;
  let inQuotes = false;

  const flush = () => {
    if (current && current.text !== "") {
      const { text, negated, qualifier, separator } = current;
      tokens.push(qualifier === undefined ? { text, negated } : { text, negated, qualifier, separator });
    }
    current = null;
  };

  for (const ch of query) {
    if (!inQuotes && /\s/.test(ch)) {
      flush();
      continue;
    }
    if (ch === '"') {
      current ??= { text: "", negated: false, sawQuote: false };
      current.sawQuote = true;
      inQuotes = !inQuotes;
      continue;
    }
    if (current === null) {
      // First character of a token: a bare minus opens an exclusion.
      if (ch === "-") {
        current = { text: "", negated: true, sawQuote: false };
        continue;
      }
      current = { text: "", negated: false, sawQuote: false };
    }
    if (
      (ch === "=" || ch === ":") &&
      !inQuotes &&
      !current.sawQuote &&
      current.qualifier === undefined &&
      current.text !== ""
    ) {
      current.qualifier = current.text;
      current.separator = ch;
      current.text = "";
      continue;
    }
    current.text += ch;
  }
  flush();

  return tokens;
}

/** The token as a plain search term — a qualifier the consumer does not know is part of the text again. */
export function literalText(token: SearchToken): string {
  return token.qualifier === undefined ? token.text : `${token.qualifier}${token.separator}${token.text}`;
}
