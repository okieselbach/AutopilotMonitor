/**
 * Allowed Local Accounts (Local Admin Analyzer) — the entry grammar and the built-in entries, mirrored
 * from `LocalAdminAllowList` (C#) through the shared manifest. An entry is an account name or a glob
 * (`*`, `?`), allowed in any role; `name|user` allows the account only while it is not a member of the
 * local Administrators group. `tests/fixtures/local-admin-allow-list/cases.json` pins both sides.
 */
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";

const GRAMMAR = SHARED_MANIFEST.localAdminAllowList;

export type AllowedRole = "any" | "user";

export interface ParsedAllowedEntry {
  pattern: string;
  role: AllowedRole;
}

type BuiltIn = (typeof GRAMMAR.builtIns)[number];

/**
 * Parses an entry exactly as the agent does: without the separator the entry is the pattern verbatim;
 * with it, only `pattern|user` parses. Null = the agent ignores the entry.
 */
export function parseAllowedEntry(entry: string): ParsedAllowedEntry | null {
  if (entry.trim() === "") return null;
  const separator = entry.indexOf(GRAMMAR.roleSeparator);
  if (separator < 0) return { pattern: entry, role: "any" };

  const name = entry.slice(0, separator);
  const qualifier = entry.slice(separator + 1);
  if (name.length === 0 || name.trim().length !== name.length || qualifier.toLowerCase() !== GRAMMAR.standardUserQualifier) {
    return null;
  }
  return { pattern: name, role: "user" };
}

export function formatAllowedEntry(pattern: string, role: AllowedRole): string {
  return role === "user" ? `${pattern}${GRAMMAR.roleSeparator}${GRAMMAR.standardUserQualifier}` : pattern;
}

/** A glob with fewer than two characters besides `*` and `?` would switch the analyzer off. */
export function isTooBroad(pattern: string): boolean {
  return /[*?]/.test(pattern) && pattern.replace(/[*?]/g, "").length < GRAMMAR.minPatternLiterals;
}

function hasControlCharacter(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code <= 0x1f || (code >= 0x7f && code <= 0x9f)) return true;
  }
  return false;
}

/** Why the backend would refuse this new entry, or null when it may be stored. */
export function validateNewEntry(entry: string): string | null {
  if (entry.trim() === "") return "Enter an account name or pattern.";
  if (entry.length > GRAMMAR.maxEntryLength) return `An entry can have at most ${GRAMMAR.maxEntryLength} characters.`;
  if (hasControlCharacter(entry)) return "An entry cannot contain control characters.";
  if (entry.trim().length !== entry.length) return "An entry cannot start or end with a space.";
  const parsed = parseAllowedEntry(entry);
  if (!parsed) return `Use an account name or pattern, optionally followed by "${GRAMMAR.roleSeparator}${GRAMMAR.standardUserQualifier}".`;
  if ([...GRAMMAR.forbiddenNameCharacters].some((c) => parsed.pattern.includes(c))) {
    return `Account names cannot contain ${[...GRAMMAR.forbiddenNameCharacters].join(" ")}.`;
  }
  if (/^s-1-\d/i.test(parsed.pattern)) return "That is a SID — enter the account name instead.";
  if (isTooBroad(parsed.pattern)) {
    return `Pattern is too broad — it needs at least ${GRAMMAR.minPatternLiterals} characters besides * and ?, otherwise the analyzer would be effectively disabled.`;
  }
  return null;
}

const BUILT_INS: readonly BuiltIn[] = GRAMMAR.builtIns;
const RID_NAMES = new Set(BUILT_INS.filter((b) => b.match === "rid").map((b) => b.name.toLowerCase()));

/** The built-in entries grouped for display. */
export const BUILT_IN_GROUPS = {
  /** Allowed in any role: the built-in Administrator (by RID, any language) and the OOBE accounts. */
  adminAllowed: BUILT_INS.filter((b) => b.match !== "folder" && b.role === "any"),
  /** Never an administrator: the RID accounts Guest/DefaultAccount/WDAGUtilityAccount and the system accounts. */
  standardUser: BUILT_INS.filter(
    (b) => b.role === "user" && (b.match === "rid" || (b.match === "account" && !RID_NAMES.has(b.name.toLowerCase()))),
  ),
  /** Other accounts that only carry a built-in account's name — allowed as standard users (decoys). */
  sharedNames: BUILT_INS.filter((b) => b.match === "account" && RID_NAMES.has(b.name.toLowerCase())),
  /** C:\Users folders that are never an account. */
  folders: BUILT_INS.filter((b) => b.match === "folder"),
};

const BUILT_IN_NAMES = new Set(BUILT_INS.map((b) => b.name.toLowerCase()));

/** True when the pattern is a built-in name — another entry for it is refused. */
export function isBuiltInName(pattern: string): boolean {
  return BUILT_IN_NAMES.has(pattern.toLowerCase());
}

/**
 * True when a stored entry adds nothing to the built-ins: a built-in name in any role (the built-in
 * already allows or restricts it — a restriction always wins), or as standard user where the built-in
 * already says so.
 */
export function isCoveredByBuiltIn(entry: string): boolean {
  const parsed = parseAllowedEntry(entry);
  if (!parsed || !isBuiltInName(parsed.pattern)) return false;
  if (parsed.role === "any") return true;
  return BUILT_INS.some((b) => b.name.toLowerCase() === parsed.pattern.toLowerCase() && b.role === "user");
}
