import { interpolateRuleTemplate } from "./interpolateRuleTemplate";

/**
 * Rule texts (explanation, remediation steps) for display. A stored finding keeps the rule's raw
 * template; this module prunes it, parses its markdown, and only then puts the recorded values into
 * text nodes — a value from the device is never read as markdown.
 *
 * The pruning is the one notifications get (`RuleTemplateInterpolator.InterpolateForNotification`),
 * pinned for both by `tests/fixtures/rule-text-display/cases.json`: a list item whose token was not
 * recorded is dropped, a token in running text that was not recorded reads "–", and a sentence that
 * still talks about literal placeholders is removed.
 *
 * Markdown is the subset the rules use: paragraphs (single newlines are line breaks), "- " or
 * "1. " list items, **bold**, *emphasis* and `code`. No links, no HTML.
 */

/** What a token reads like when this finding did not record it. */
export const NOT_RECORDED = "–";

export type RuleInline =
  | { kind: "text"; text: string }
  | { kind: "strong"; children: RuleInline[] }
  | { kind: "em"; children: RuleInline[] }
  | { kind: "code"; children: RuleInline[] }
  /** A template token, as written (template view without evidence). */
  | { kind: "token"; token: string }
  /** A token's value for this finding; `recorded` false when it was not recorded. */
  | { kind: "value"; text: string; recorded: boolean };

export type RuleBlock =
  | { kind: "paragraph"; lines: RuleInline[][] }
  | { kind: "list"; ordered: boolean; items: RuleInline[][] };

/** The token's recorded value, or null when this finding did not record it. */
export type TokenResolver = (token: string) => string | null;

const TOKEN = /\{\{\s*[a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)?\s*\}\}/g;
const TOKEN_AT_START = /^\{\{\s*[a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)?\s*\}\}/;
const LIST_ITEM = /^\s*(?:[-*+]|\d+[.)])\s+/;
const ORDERED_ITEM = /^\s*\d+[.)]\s+/;
const SENTENCE_BREAK = /(?<=[.!?])\s+/;

/** Resolves tokens from a finding's evidence; an unresolved token comes back from the interpolation as written. */
export function tokenResolver(matchedConditions: Record<string, unknown> | null | undefined): TokenResolver {
  return (token) => {
    const value = interpolateRuleTemplate(token, matchedConditions);
    return value === token ? null : value;
  };
}

/**
 * Drops what a reader without the placeholders cannot use. Works on the template, so the result
 * still carries its tokens for the parser.
 */
export function pruneRuleText(text: string, resolve: TokenResolver): string {
  const kept: string[] = [];
  for (const line of text.replace(/\r\n/g, "\n").split("\n")) {
    let unresolved = false;
    const probe = (s: string) =>
      s.replace(TOKEN, (token) => {
        const value = resolve(token);
        if (value !== null) return value;
        unresolved = true;
        return NOT_RECORDED;
      });
    const rendered = probe(line);
    if (unresolved && LIST_ITEM.test(line)) continue;

    if (!rendered.includes("{{")) {
      kept.push(line);
      continue;
    }
    // A sentence that still shows "{{" after filling talks about placeholders themselves.
    const sentences = line.split(SENTENCE_BREAK).filter((sentence) => !probe(sentence).includes("{{"));
    if (sentences.length > 0) kept.push(sentences.join(" "));
  }
  return kept.join("\n").replace(/\n{3,}/g, "\n\n").trim();
}

/** Paragraphs and lists of a rule text; tokens stay tokens. */
export function parseRuleText(text: string): RuleBlock[] {
  const blocks: RuleBlock[] = [];
  let paragraph: RuleInline[][] | null = null;
  let list: Extract<RuleBlock, { kind: "list" }> | null = null;

  for (const raw of text.replace(/\r\n/g, "\n").split("\n")) {
    if (raw.trim() === "") {
      paragraph = null;
      list = null;
      continue;
    }
    const item = LIST_ITEM.exec(raw);
    if (item) {
      paragraph = null;
      const ordered = ORDERED_ITEM.test(raw);
      if (!list || list.ordered !== ordered) {
        list = { kind: "list", ordered, items: [] };
        blocks.push(list);
      }
      list.items.push(parseInline(raw.slice(item[0].length).trim()));
      continue;
    }
    list = null;
    if (!paragraph) {
      paragraph = [];
      blocks.push({ kind: "paragraph", lines: paragraph });
    }
    paragraph.push(parseInline(raw.trim()));
  }
  return blocks;
}

/** Inline markdown of one line: **bold**, *emphasis*, `code` (tokens only inside) and tokens. */
export function parseInline(s: string): RuleInline[] {
  const out: RuleInline[] = [];
  let text = "";
  const flush = () => {
    if (text) out.push({ kind: "text", text });
    text = "";
  };

  let i = 0;
  while (i < s.length) {
    if (s.startsWith("{{", i)) {
      const token = TOKEN_AT_START.exec(s.slice(i));
      if (token) {
        flush();
        out.push({ kind: "token", token: token[0] });
        i += token[0].length;
        continue;
      }
    }
    if (s[i] === "`") {
      const end = s.indexOf("`", i + 1);
      if (end > i + 1) {
        flush();
        out.push({ kind: "code", children: parseTokensOnly(s.slice(i + 1, end)) });
        i = end + 1;
        continue;
      }
    }
    if (s.startsWith("**", i)) {
      const end = s.indexOf("**", i + 2);
      if (end > i + 2) {
        flush();
        out.push({ kind: "strong", children: parseInline(s.slice(i + 2, end)) });
        i = end + 2;
        continue;
      }
    }
    if (s[i] === "*" && s[i + 1] !== undefined && s[i + 1] !== "*" && !/\s/.test(s[i + 1])) {
      const end = closingEmphasis(s, i + 1);
      if (end > 0) {
        flush();
        out.push({ kind: "em", children: parseInline(s.slice(i + 1, end)) });
        i = end + 1;
        continue;
      }
    }
    text += s[i];
    i++;
  }
  flush();
  return out;
}

/** Fills every token with its value (or the not-recorded mark). */
export function fillRuleText(blocks: RuleBlock[], resolve: TokenResolver): RuleBlock[] {
  return blocks.map((block) =>
    block.kind === "paragraph"
      ? { kind: "paragraph", lines: block.lines.map((line) => fillInlines(line, resolve)) }
      : { ...block, items: block.items.map((item) => fillInlines(item, resolve)) }
  );
}

/** A finding's rule text, ready to render: pruned, parsed, values filled in. */
export function ruleTextBlocks(
  text: string | null | undefined,
  matchedConditions: Record<string, unknown> | null | undefined
): RuleBlock[] {
  if (!text) return [];
  const resolve = tokenResolver(matchedConditions);
  return fillRuleText(parseRuleText(pruneRuleText(text, resolve)), resolve);
}

/**
 * One remediation step, filled in; null when it names a value this finding did not record —
 * a step is a list item, and a list item without its value is dropped.
 */
export function ruleStepInlines(
  step: string | null | undefined,
  matchedConditions: Record<string, unknown> | null | undefined
): RuleInline[] | null {
  if (!step?.trim()) return null;
  const resolve = tokenResolver(matchedConditions);
  const tokens = step.match(TOKEN) ?? [];
  if (tokens.some((token) => resolve(token) === null)) return null;
  return fillInlines(parseInline(step.trim()), resolve);
}

/** Plain text of parsed blocks (copy to clipboard): markers dropped, values in, lists as "- " lines. */
export function ruleTextPlain(blocks: RuleBlock[]): string {
  return blocks
    .map((block) =>
      block.kind === "paragraph"
        ? block.lines.map(inlinePlain).join("\n")
        : block.items.map((item, i) => `${block.ordered ? `${i + 1}.` : "-"} ${inlinePlain(item)}`).join("\n")
    )
    .join("\n\n");
}

export function inlinePlain(inlines: RuleInline[]): string {
  return inlines
    .map((node) => {
      switch (node.kind) {
        case "text":
          return node.text;
        case "token":
          return node.token;
        case "value":
          return node.text;
        default:
          return inlinePlain(node.children);
      }
    })
    .join("");
}

function parseTokensOnly(s: string): RuleInline[] {
  const out: RuleInline[] = [];
  let last = 0;
  for (const match of s.matchAll(TOKEN)) {
    const at = match.index ?? 0;
    if (at > last) out.push({ kind: "text", text: s.slice(last, at) });
    out.push({ kind: "token", token: match[0] });
    last = at + match[0].length;
  }
  if (last < s.length) out.push({ kind: "text", text: s.slice(last) });
  return out;
}

/** The closing single "*" of an emphasis, not part of "**" and not after a space; -1 when none. */
function closingEmphasis(s: string, from: number): number {
  for (let j = from; j < s.length; j++) {
    if (s[j] !== "*") continue;
    if (s[j + 1] === "*") {
      j++;
      continue;
    }
    if (j > from && !/\s/.test(s[j - 1])) return j;
  }
  return -1;
}

function fillInlines(inlines: RuleInline[], resolve: TokenResolver): RuleInline[] {
  return inlines.map((node): RuleInline => {
    switch (node.kind) {
      case "token": {
        const value = resolve(node.token);
        return value === null
          ? { kind: "value", text: NOT_RECORDED, recorded: false }
          : { kind: "value", text: value, recorded: true };
      }
      case "strong":
      case "em":
      case "code":
        return { ...node, children: fillInlines(node.children, resolve) };
      default:
        return node;
    }
  });
}
