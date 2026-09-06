/**
 * Error-code catalog for the MCP server — the same `error-codes.json` (schemaVersion 2) the
 * backend embeds and the web app bundles, read once from disk (ERROR_CODES_PATH, in the
 * container a COPY of the Shared resource; locally the Shared resource itself).
 *
 * Deliberately NOT part of the vector index: ~600 one-line documents would crowd the rules out
 * of `search_knowledge` for queries like "access denied during install", and every catalog
 * change would force a re-embed. The catalog answers point lookups (`lookup_error_code`) and
 * joins the literal error-code fallback of `search_knowledge` as lexical documents.
 *
 * Resolution mirrors the C# `ErrorCodeCatalog`: direct key, hex normalisation, signed-decimal
 * HRESULT, then the HRESULT_FROM_WIN32 derivation (`0x80070643` → `1603`, reported as
 * `derivedFromWin32`). Symbols and enforcement-state names resolve through their own indexes.
 */
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { SearchDocument } from './search-provider.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

/** Default: the Shared resource, four levels up from src/ or dist/ (repo layout). */
export const DEFAULT_ERROR_CODES_PATH =
  process.env.ERROR_CODES_PATH ??
  resolve(__dirname, '..', '..', '..', 'Shared', 'AutopilotMonitor.Shared', 'Resources', 'error-codes.json');

export const ERROR_CODE_SCHEMA_VERSION = 2;

export interface ErrorCodeEntry {
  description: string;
  confidence: 'high' | 'medium' | 'low';
  source: string;
  category: string;
  symbol?: string;
  imeRetriesDuringEsp?: boolean;
}

export interface EnforcementStateEntry {
  value: string;
  name: string;
  description: string;
}

export interface ErrorCodeCatalog {
  entries: Map<string, ErrorCodeEntry>;
  enforcementStates: Map<string, EnforcementStateEntry>;
  bySymbol: Map<string, string>;
  statesByName: Map<string, EnforcementStateEntry>;
  size: number;
}

/** Parse a catalog file. Throws on a missing file or a schema the server does not speak. */
export function readErrorCodeCatalog(filePath: string): ErrorCodeCatalog {
  const raw = JSON.parse(readFileSync(filePath, 'utf8')) as {
    schemaVersion?: number;
    entries?: Record<string, ErrorCodeEntry>;
    enforcementStates?: Record<string, { name: string; description: string }>;
  };
  if (raw.schemaVersion !== ERROR_CODE_SCHEMA_VERSION) {
    throw new Error(`${filePath}: schemaVersion ${raw.schemaVersion}, expected ${ERROR_CODE_SCHEMA_VERSION}`);
  }
  if (!raw.entries) throw new Error(`${filePath}: no entries`);

  const entries = new Map<string, ErrorCodeEntry>();
  const bySymbol = new Map<string, string>();
  for (const [key, entry] of Object.entries(raw.entries)) {
    const k = key.toLowerCase();
    entries.set(k, entry);
    if (entry.symbol && !bySymbol.has(entry.symbol.toLowerCase())) bySymbol.set(entry.symbol.toLowerCase(), k);
  }
  const enforcementStates = new Map<string, EnforcementStateEntry>();
  const statesByName = new Map<string, EnforcementStateEntry>();
  for (const [value, s] of Object.entries(raw.enforcementStates ?? {})) {
    const state = { value, name: s.name, description: s.description };
    enforcementStates.set(value, state);
    if (!statesByName.has(s.name.toLowerCase())) statesByName.set(s.name.toLowerCase(), state);
  }
  return { entries, enforcementStates, bySymbol, statesByName, size: entries.size };
}

let cached: ErrorCodeCatalog | undefined;
/** The process-wide catalog (loaded on first use, from DEFAULT_ERROR_CODES_PATH). */
export function getErrorCodeCatalog(): ErrorCodeCatalog {
  if (!cached) cached = readErrorCodeCatalog(DEFAULT_ERROR_CODES_PATH);
  return cached;
}

// ── Lookup ─────────────────────────────────────────────────────────────

export interface ErrorCodeHit {
  found: true;
  input: string;
  /** Catalog key that matched ("0x80070005", "1603"). */
  key: string;
  /** 8-digit hex form of the code (for a decimal MSI key: its HRESULT_FROM_WIN32 form). */
  normalizedHex: string;
  /** Signed 32-bit decimal as the IME logs print it (for MSI keys: the exit code itself). */
  decimal: number;
  symbol?: string;
  category: string;
  description: string;
  confidence: ErrorCodeEntry['confidence'];
  source: string;
  /** Set when a 0x8007xxxx input resolved through its low word to the decimal MSI entry. */
  derivedFromWin32?: number;
  imeRetriesDuringEsp?: boolean;
}

export interface EnforcementStateHit {
  found: true;
  input: string;
  enforcementState: EnforcementStateEntry;
}

export interface ErrorCodeMiss {
  found: false;
  input: string;
  normalizedHex?: string;
  note: string;
}

export type ErrorCodeLookupResult = ErrorCodeHit | EnforcementStateHit | ErrorCodeMiss;

const hexKey = (n: number): string => '0x' + (n >>> 0).toString(16).padStart(8, '0');
const signed32 = (n: number): number => (n >>> 0) | 0;
const isMsiDecimal = (n: number): boolean => n === 0 || (n >= 1601 && n <= 1654) || n === 3010;

function hit(input: string, catalog: ErrorCodeCatalog, key: string, derivedFromWin32?: number): ErrorCodeHit {
  const entry = catalog.entries.get(key)!;
  const isHex = key.startsWith('0x');
  const value = isHex ? parseInt(key.slice(2), 16) : Number(key);
  return {
    found: true,
    input,
    key,
    normalizedHex: isHex ? key : hexKey(value === 0 ? 0 : 0x80070000 + value),
    decimal: isHex ? signed32(value) : value,
    ...(entry.symbol ? { symbol: entry.symbol } : {}),
    category: entry.category,
    description: entry.description,
    confidence: entry.confidence,
    source: entry.source,
    ...(derivedFromWin32 !== undefined ? { derivedFromWin32 } : {}),
    ...(entry.imeRetriesDuringEsp ? { imeRetriesDuringEsp: true } : {}),
  };
}

/**
 * Resolve one code: hex (`0x87D30067`, `87d30067`), signed or unsigned decimal
 * (`-2016214937`, `1603`), a symbol (`ERROR_INSTALL_FAILURE`, `UnzipError`) or an IME
 * enforcement-state name or number (`NotAttemptedDependencyWithFailure`, `6001`).
 */
export function lookupErrorCode(rawInput: string, catalog: ErrorCodeCatalog = getErrorCodeCatalog()): ErrorCodeLookupResult {
  const input = String(rawInput ?? '').trim();
  if (!input) return { found: false, input, note: 'Empty input.' };
  const lowered = input.toLowerCase();

  // Symbol / enforcement-state name
  if (/^[a-z_][a-z0-9_]*$/i.test(input) && !/^[0-9a-f]{8}$/i.test(input)) {
    const symbolKey = catalog.bySymbol.get(lowered);
    if (symbolKey) return hit(input, catalog, symbolKey);
    const state = catalog.statesByName.get(lowered);
    if (state) return { found: true, input, enforcementState: state };
    return { found: false, input, note: 'No catalog entry carries this symbol or enforcement-state name.' };
  }

  // Direct key (decimal MSI exit code or already-normalised hex)
  if (catalog.entries.has(lowered)) return hit(input, catalog, lowered);

  let hex: string | undefined;
  if (/^0x[0-9a-f]{1,8}$/i.test(input)) {
    hex = hexKey(parseInt(input.slice(2), 16));
  } else if (/^[0-9a-f]{8}$/i.test(input) && /[a-f]/i.test(input)) {
    hex = hexKey(parseInt(input, 16));
  } else if (/^-?\d+$/.test(input)) {
    const n = Number(input);
    if (n < 0 && n >= -2147483648) {
      hex = hexKey(n + 0x100000000);
    } else if (n >= 0 && n <= 0xffffffff) {
      // A positive decimal outside the MSI range: an enforcement state (1000, 6001) or an
      // unsigned HRESULT (2278556700). Small numbers are installer-defined exit codes → unknown.
      const state = catalog.enforcementStates.get(String(n));
      if (state) return { found: true, input, enforcementState: state };
      if (n > 0xffff) hex = hexKey(n);
      else if (!isMsiDecimal(n)) return { found: false, input, note: 'Decimal exit codes outside the MSI range are installer-defined; the catalog does not gloss them.' };
    }
  }
  if (!hex) return { found: false, input, note: 'Not a hex code, a decimal HRESULT, an MSI exit code, a symbol or an enforcement state.' };

  if (catalog.entries.has(hex)) return hit(input, catalog, hex);

  // HRESULT_FROM_WIN32 derivation: facility 7, low word ≠ 0, decimal MSI entry present.
  if (hex.startsWith('0x8007')) {
    const low = parseInt(hex.slice(6), 16);
    if (low !== 0 && catalog.entries.has(String(low))) return hit(input, catalog, String(low), low);
  }
  return { found: false, input, normalizedHex: hex, note: 'Unknown code — not in the catalog.' };
}

// ── Lexical documents for the search_knowledge fallback ──────────────

let cachedDocs: SearchDocument[] | undefined;
/** One short document per entry, flagged `type: "error-code"`, for literal needle matching only. */
export function errorCodeSearchDocuments(catalog: ErrorCodeCatalog = getErrorCodeCatalog()): SearchDocument[] {
  if (catalog === cached && cachedDocs) return cachedDocs;
  const docs: SearchDocument[] = [];
  for (const [key, entry] of catalog.entries) {
    const shownKey = key.startsWith('0x') ? '0x' + key.slice(2).toUpperCase() : key;
    const title = [shownKey, entry.symbol].filter(Boolean).join(' ');
    docs.push({
      id: `error-code:${key}`,
      text: `${title}: ${entry.description} [${entry.category}]`,
      metadata: {
        type: 'error-code',
        title,
        category: entry.category,
        confidence: entry.confidence,
        source: entry.source,
        ...(entry.symbol ? { symbol: entry.symbol } : {}),
      },
    });
  }
  if (catalog === cached) cachedDocs = docs;
  return docs;
}
